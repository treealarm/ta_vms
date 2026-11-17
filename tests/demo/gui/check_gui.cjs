/* Acceptance through the browser: what check_demo.py cannot see.

   check_demo.py walks the demo over REST, so it proves the API answers; this proves the operator's
   own clicks reach that API. That is a thin layer, and the bugs live in it — M2.11's part placement
   worked in the code and not in the GUI, because the palette's pick never armed the click and the
   landmarks kept catching it. No API check could have noticed.

   It is an acceptance tool, not a regression suite: it needs the whole dev stack and the simulator
   running, it is slow, and it changes the demo (and puts it back). Run it by hand at a milestone,
   beside check_demo.py.

     cd tests/demo/gui && npm install && npx playwright install chromium
     node check_gui.cjs                     # VMS_WEB defaults to http://localhost:5173

   Environment: VMS_WEB, VMS_USER, VMS_PASS, SHOTS (screenshots; ./shots by default). */
const { chromium } = require('playwright')
const path = require('path')

const BASE = process.env.VMS_WEB || 'http://localhost:5173'
const USER = process.env.VMS_USER || 'admin'
const PASS = process.env.VMS_PASS || 'admin'
const SHOTS = process.env.SHOTS || path.join(__dirname, 'shots')

const results = []
function check(ok, what, detail = '') {
  results.push({ ok, what, detail })
  console.log(`${ok ? '  ok  ' : ' FAIL '} ${what}${detail ? ` — ${detail}` : ''}`)
  return ok
}

async function shot(page, name) {
  await page.screenshot({ path: path.join(SHOTS, name) }).catch(() => {})
}

async function login(page) {
  await page.goto(`${BASE}/login`, { waitUntil: 'domcontentloaded' })
  const inputs = page.locator('input')
  await inputs.nth(0).fill(USER)
  await inputs.nth(1).fill(PASS)
  await page.getByRole('button', { name: /sign in/i }).click()
  await page.waitForURL(/\/admin|\/operator/, { timeout: 20000 })
}

/**
 * The demo's nodes, read through the same session the browser holds: a space's id is the server's
 * and a picture's is made on every install, so nothing here may be baked into the script.
 */
async function demoNodes(page) {
  return page.evaluate(async () => {
    const get = async (url) => {
      const response = await fetch(url)
      if (!response.ok) throw new Error(`${url} answered ${response.status}`)
      return response.json()
    }
    const spaces = await get('/api/spatial/spaces')
    const map1 = spaces.find((space) => space.name === 'map1')
    const plan1 = spaces.find((space) => space.name === 'plan1')
    if (!map1 || !plan1) throw new Error('the demo world is not installed: map1/plan1 are missing')
    const kids = async (id) =>
      (await get(`/api/map/objects/parent?parentId=${id}&count=1000`)).children ?? []
    const cameras = await get('/api/live/cameras')
    const cam1 = cameras.find((camera) => camera.name === 'cam1')
    if (!cam1) throw new Error('camera cam1 is not provisioned')

    const pictures = (await kids(map1.id))
      .filter((node) => node.nodeKind === 'part' && /^relay4 rep\d+$/.test(node.name ?? ''))
      .sort((a, b) => Number(a.name.split('rep')[1]) - Number(b.name.split('rep')[1]))
    const cameraPicture = (await kids(plan1.id)).find((node) => node.cameraId === cam1.cameraId)

    return { relay: pictures[0], cameraPicture, camera: cam1 }
  })
}

/** The Material switch as the browser really has it: a checkbox input, not an aria-checked div. */
const guardSwitch = (page) => page.locator('[role="switch"]').first()

async function setSwitch(page, want, timeout = 20000) {
  const sw = guardSwitch(page)
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    // The inspector asks the server what the object is before it can offer a switch; a missing
    // switch is "not yet", never "off".
    if ((await sw.count()) > 0) {
      if ((await sw.isChecked()) === want) return true
      await sw.click()
      await page.waitForTimeout(1500)
    } else {
      await page.waitForTimeout(500)
    }
  }
  return false
}

/** Open a node on the operator's map by its deep link — the way the journal opens one. */
async function openOnMap(page, nodeId) {
  await page.goto(`${BASE}/operator/map?node=${nodeId}`, { waitUntil: 'domcontentloaded' })
}

/** The alarms whose condition holds right now, read through the session's own API: the map's list
    is what the operator sees, but it refreshes on a poll and counts rows, which a full list drops. */
const activeAlarms = (page) => page.evaluate(async () => {
  const response = await fetch('/api/map/alarms', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pageSize: 100 }),
  })
  if (!response.ok) throw new Error(`/api/map/alarms answered ${response.status}`)
  const data = await response.json()
  return (data.open?.alarms ?? []).filter((alarm) => alarm.status === 'active').length
})

/** The relay4 pictures standing on map1, by id — a diff around the placement click is what it made. */
const relayPictureIds = (page) => page.evaluate(async () => {
  const spaces = await (await fetch('/api/spatial/spaces')).json()
  const map1 = spaces.find((space) => space.name === 'map1')
  const page_ = await (await fetch(`/api/map/objects/parent?parentId=${map1.id}&count=1000`)).json()
  return (page_.children ?? [])
    .filter((node) => node.nodeKind === 'part' && /^relay4 rep\d+$/.test(node.name ?? ''))
    .map((node) => node.id)
})

/** Taking away what this check placed is housekeeping, not acceptance: the session's own API. */
const deleteNodes = (page, ids) => page.evaluate(async (ids) => {
  await fetch('/api/map/admin/objects', {
    method: 'DELETE',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids }),
  })
}, ids)

;(async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } })
  page.setDefaultTimeout(30000)

  try {
    await login(page)
    check(true, 'the browser signs in')
    const nodes = await demoNodes(page)

    // ── 1. The relay on the map, switched from its inspector ──────────────────
    try {
      if (!check(nodes.relay, 'the world places relay4 on the map')) throw new Error('no picture')
      await openOnMap(page, nodes.relay.id)
      await page.getByText(nodes.relay.name, { exact: true }).first().waitFor({ timeout: 30000 })
      const appeared = await guardSwitch(page).waitFor({ timeout: 20000 })
        .then(() => true).catch(() => false)
      const switches = appeared ? await page.locator('[role="switch"]').count() : 0
      check(switches === 1, 'the map inspector offers one switch for the relay', `${switches} switches`)
      check(await setSwitch(page, false), 'the relay starts off')
      await shot(page, '01-map-relay.png')

      check(await setSwitch(page, true), 'the switch on the map turns the relay on')
      await shot(page, '02-map-relay-active.png')
    } catch (e) {
      check(false, 'the relay on the map', e.message)
    }

    // ── 2. The button raises the alarm the map lists ──────────────────────────
    // An alarm is the condition, not the arming: it stays open while the contact is closed and
    // closes only when the contact opens. So the camera is armed with the relay off, and the
    // alarm is the relay's own doing — which is also the operator's story.
    try {
      // A camera is guarded from the Control panel (its row's switch), not from the map's
      // inspector, which guards a space or a part.
      await page.goto(`${BASE}/operator/control`, { waitUntil: 'domcontentloaded' })
      const cameraRow = page.locator('[role="button"]').filter({
        has: page.locator('span.MuiListItemText-primary', { hasText: /^cam1$/ }),
      }).first()
      await cameraRow.waitFor({ timeout: 30000 })
      await cameraRow.click()
      await page.waitForTimeout(1500)
      check(await setSwitch(page, true), 'the camera is armed from the Control panel')
      await shot(page, '03-camera-armed.png')

      await openOnMap(page, nodes.relay.id)
      await page.getByText(nodes.relay.name, { exact: true }).first().waitFor({ timeout: 30000 })
      check(await setSwitch(page, false), 'the relay is off: the sensor is open')
      await page.waitForTimeout(3000)
      const before = await activeAlarms(page)
      check(before === 0, 'nothing is alarming while the contact is open', `${before} active`)

      await page.getByText('Alarms', { exact: true }).first().click()
      check(await setSwitch(page, true), 'the relay is switched on from the map')

      const deadline = Date.now() + 60000
      let after = before
      while (Date.now() < deadline) {
        after = await activeAlarms(page)
        if (after > before) break
        await page.waitForTimeout(2000)
      }
      check(after > before, 'the closed contact raises an alarm', `${before} → ${after} active`)

      // And the operator sees it: the map's list names the sensor's live condition.
      const listed = await page.getByText(/Sensor · Active/).first()
        .waitFor({ timeout: 30000 }).then(() => true).catch(() => false)
      check(listed, 'the map lists the alarm as Sensor · Active')
      await shot(page, '04-sensor-alarm.png')

      // The contact is opened again here, so the checks after this one run on a quiet demo.
      check(await setSwitch(page, false), 'the relay is switched off, the alarm with it')
    } catch (e) {
      check(false, 'the alarm through the browser', e.message)
    }

    // ── 3. Placing a part from the palette ────────────────────────────────────
    try {
      await page.goto(`${BASE}/admin/maps`, { waitUntil: 'domcontentloaded' })
      await page.getByText('map1', { exact: true }).first().click()
      await page.locator('.leaflet-container').waitFor({ timeout: 30000 })

      // Devices is the default tab; open the camera and pick its fourth relay.
      const camRow = page.locator('[role="button"]').filter({ hasText: /^cam1/ }).first()
      await camRow.waitFor({ timeout: 30000 })
      await camRow.getByRole('button').first().click() // the chevron: read the parts
      const partRow = page.locator('[role="button"]').filter({ hasText: 'relay4' }).first()
      await partRow.waitFor({ timeout: 30000 })
      await partRow.click()

      const picking = await page.getByText(/Click the map to place relay4/i)
        .waitFor({ timeout: 10000 }).then(() => true).catch(() => false)
      check(picking, 'picking a part from the palette puts the editor into placement')
      await shot(page, '05-palette-picking.png')
      if (!picking) throw new Error('the click was not armed')

      // A click on empty map, clear of the world's own pictures.
      const before = await relayPictureIds(page)
      const box = await page.locator('.leaflet-container').boundingBox()
      await page.mouse.click(box.x + box.width * 0.3, box.y + box.height * 0.78)
      await page.waitForTimeout(2500)
      const placed = await page.getByText(/^relay4 rep\d+$/, { exact: false }).last()
        .waitFor({ timeout: 15000 }).then(() => true).catch(() => false)
      check(placed, 'the click places a replica, named by the server')
      await shot(page, '06-part-placed.png')

      // The demo's own picture stays; what this check placed goes, so a second run starts clean.
      const added = (await relayPictureIds(page)).filter((id) => !before.includes(id))
      check(added.length === 1, 'exactly one replica appeared', `${added.length} new`)
      if (added.length) await deleteNodes(page, added)
    } catch (e) {
      check(false, 'placing a part from the palette', e.message)
    }

    // ── 4. The camera's rear panel ────────────────────────────────────────────
    try {
      await openOnMap(page, nodes.cameraPicture.id)
      await page.getByRole('button', { name: /Device diagram/i }).click()
      await page.waitForTimeout(3000)
      // The socket names are painted into the panel's picture, an <img>; what the page puts in the
      // DOM is one element per bound part, each titled with its name and its states.
      for (const part of ['sensor1', 'relay1', 'relay2', 'relay3', 'relay4']) {
        const element = page.locator(`[title^="${part}"]`).first()
        const there = await element.waitFor({ timeout: 8000 }).then(() => true).catch(() => false)
        check(there, `the rear panel binds ${part} to its socket`)
      }
      await shot(page, '07-camera-panel.png')
    } catch (e) {
      check(false, 'the camera panel', e.message)
    }

    // ── 5. Put the demo back: the relay off, the camera off guard ─────────────
    try {
      await openOnMap(page, nodes.relay.id)
      await page.getByText(nodes.relay.name, { exact: true }).first().waitFor({ timeout: 30000 })
      check(await setSwitch(page, false), 'the relay is switched off again')

      await page.goto(`${BASE}/operator/control`, { waitUntil: 'domcontentloaded' })
      const cameraRow = page.locator('[role="button"]').filter({
        has: page.locator('span.MuiListItemText-primary', { hasText: /^cam1$/ }),
      }).first()
      await cameraRow.waitFor({ timeout: 30000 })
      await cameraRow.click()
      await page.waitForTimeout(1500)
      check(await setSwitch(page, false), 'the camera is left off guard')
    } catch (e) {
      check(false, 'putting the demo back', e.message)
    }
  } finally {
    await browser.close()
  }

  const failed = results.filter((result) => !result.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  if (failed.length) {
    console.log('Screenshots: ' + SHOTS)
  }
  process.exit(failed.length === 0 ? 0 : 1)
})().catch((e) => {
  console.error(e)
  process.exit(1)
})

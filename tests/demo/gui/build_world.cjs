/* Build the parity world through the browser, and nothing else.

   The demo is the proof by construction: its seeder writes through the ordinary services, so an
   administrator must be able to write an equivalent element through the GUI. This script drives
   that path for one instance of every configuration primitive — not the demo's four racks and
   192 ports, but one of each kind — and screenshots every step.

     node build_world.cjs --phase demo      # install the demo (the coverage baseline)
     node build_world.cjs --phase build     # remove it, then build the parity world
     node build_world.cjs --phase continue  # finish a half-built world: rights, calibration,
                                            # wall, routing, binding (--steps narrows it)
     node build_world.cjs --phase cleanup   # delete what the build made
     node build_world.cjs --reset-db --force  # drop and recreate ta_map/ta_cfg on the dev bench

   Environment: VMS_WEB (default http://localhost:5173), VMS_USER/VMS_PASS, PREFIX (parity),
   ONVIF_HOST (127.0.0.1), ONVIF_PORT_BASE (8101), SIM_URL (http://127.0.0.1:8090),
   SIM_PTZ_CAMERA (ptz), SHOTS (./shots/parity). The stack must run with the demo off:
   scripts/dev-up.sh --no-demo. */
const { chromium } = require('playwright')
const { execFileSync } = require('child_process')
const path = require('path')

const REPO = path.resolve(__dirname, '../../..')

const BASE = process.env.VMS_WEB || 'http://localhost:5173'
const USER = process.env.VMS_USER || 'admin'
const PASS = process.env.VMS_PASS || 'admin'
const PREFIX = process.env.PREFIX || 'parity'
const ONVIF_HOST = process.env.ONVIF_HOST || '127.0.0.1'
const ONVIF_PORT_BASE = Number(process.env.ONVIF_PORT_BASE || 8101)
const SHOTS = process.env.SHOTS || path.join(__dirname, 'shots', PREFIX)
const SIM_URL = process.env.SIM_URL || 'http://127.0.0.1:8090'
const SIM_PTZ_CAMERA = process.env.SIM_PTZ_CAMERA || 'ptz'
const FIXTURES = path.join(__dirname, 'fixtures')

const MAP = `${PREFIX}-map`
const PLAN = `${PREFIX}-plan`
const MASTER = `${PREFIX}-master`
const PTZ = `${PREFIX}-ptz`
const VEHICLE = `${PREFIX}-vehicle`
const WALL = `${PREFIX}-wall`
const RULE = `${PREFIX}-sensor-rule`
const LEVEL = `${PREFIX}-ground`

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

/** Reads through the session the browser holds: ids and expectations, never a world mutation. */
async function api(page, method, url, body) {
  return page.evaluate(async ([m, u, b]) => {
    const response = await fetch(u, {
      method: m,
      headers: b === undefined ? {} : { 'Content-Type': 'application/json' },
      body: b === undefined ? undefined : JSON.stringify(b),
    })
    if (!response.ok) return { error: response.status }
    return response.json().catch(() => ({}))
  }, [method, url, body])
}

const spaces = (page) => api(page, 'GET', '/api/spatial/spaces')
const cameras = (page) => api(page, 'GET', '/api/live/cameras')
const levels = async (page) => (await api(page, 'GET', '/api/map/levels')).levels || []

async function spaceByName(page, name) {
  return (await spaces(page)).find((space) => space.name === name)
}

async function openMaps(page, spaceName) {
  await page.goto(`${BASE}/admin/maps`, { waitUntil: 'domcontentloaded' })
  await page.getByText('Spaces', { exact: true }).waitFor({ timeout: 30000 })
  if (spaceName) await page.getByText(spaceName, { exact: false }).first().click()
  await page.waitForTimeout(1200)
}

async function confirmOnce(page) {
  // The browser has one handler that accepts every confirmation: these are the destructive steps
  // this script asked for, and a handler left over from a dialog that never came would fight the
  // next one.
}

async function addMenuItem(page, label) {
  await page.locator('button[aria-label="Add"]').first().click()
  await page.getByRole('menuitem', { name: label }).click()
  await page.waitForTimeout(300)
  // The item's own navigation may leave the popover open for a frame; a second Add click would be
  // read as the header's button.
  await page.keyboard.press('Escape').catch(() => {})
  await page.waitForTimeout(300)
}

/** The form's Apply carries the same label as the list header's Add: the second is the form's. */
async function applyAdd(page) {
  const adds = page.getByRole('button', { name: 'Add', exact: true })
  if (await adds.count() > 1) await adds.last().click()
}

async function applyButton(page) {
  const button = page.getByRole('button', { name: 'Apply', exact: true })
  await button.waitFor({ timeout: 15000 })
  await button.click()
  await page.waitForTimeout(1500)
}

async function leafletClick(page, fx, fy) {
  const box = await page.locator('.leaflet-container').boundingBox()
  await page.mouse.click(box.x + box.width * fx, box.y + box.height * fy)
  await page.waitForTimeout(1200)
}

async function deleteCamera(page, name) {
  const row = page.locator('[role="button"]').filter({ hasText: new RegExp(`^${name}`) }).first()
  if (await row.count() === 0) return false
  await row.click()
  await page.waitForTimeout(800)
  await confirmOnce(page)
  await page.getByRole('button', { name: /^delete camera$/i }).click()
  await page.waitForTimeout(1500)
  return true
}

async function createCamera(page, { name, port, telemetry, geoFromDevice }) {
  await page.goto(`${BASE}/admin/cameras`, { waitUntil: 'domcontentloaded' })
  await page.getByText('vms', { exact: true }).first().waitFor({ timeout: 30000 })
  await addMenuItem(page, /^New camera on vms$/)
  await page.getByLabel(/Camera name/).fill(name)
  await page.getByLabel('IP', { exact: true }).fill(ONVIF_HOST)
  await page.getByLabel('Port', { exact: true }).fill(String(port))
  await page.getByLabel('User', { exact: true }).fill('admin')
  await page.getByLabel('Password', { exact: true }).fill('admin')
  if (telemetry) await page.getByLabel(/Has telemetry/).check()
  if (geoFromDevice) await page.getByLabel(/Position from the device/).check()
  await page.getByRole('button', { name: /^add camera$/i }).click()
  await page.waitForTimeout(3000)
  return (await cameras(page)).find((camera) => camera.name === name)
}

async function addStream(page, cameraName, streamName = 'main', token = 'Profile_2') {
  await page.getByText(cameraName, { exact: false }).first().click()
  await page.waitForTimeout(800)
  await addMenuItem(page, new RegExp(`^New stream on ${cameraName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`))
  await page.getByLabel(/Stream name/).fill(streamName)
  await page.getByLabel(/ONVIF profile token/).fill(token)
  await page.getByLabel(/WebRTC live stream/).check()
  await page.getByRole('button', { name: /^add stream$/i }).click()
  await page.waitForTimeout(2000)
}

async function nameParts(page, cameraName, inputName, relayPrefix) {
  await page.getByText(cameraName, { exact: false }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('tab', { name: 'Devices', exact: true }).click()
  await page.waitForTimeout(2500)
  const names = page.getByLabel('Name', { exact: true })
  const saves = page.getByRole('button', { name: 'Save part name', exact: true })
  await names.first().fill(inputName)
  await saves.first().click()
  await page.waitForTimeout(1500)
  const relays = await api(page, 'GET', `/api/DeviceIo/cameras/${
    (await cameras(page)).find((c) => c.name === cameraName).cameraId}/relay-outputs`)
  for (let index = 0; index < relays.length; index += 1) {
    await names.nth(index + 1).fill(`${relayPrefix}${index + 1}`)
    await saves.nth(index + 1).click()
    await page.waitForTimeout(800)
  }
  await shot(page, `parts-${cameraName}.png`)
}

/** The simulator keeps its relay state between runs, and the master's peer relay is wired to the
 *  slave's input: a contact left closed arms that input into an alarm the moment it is configured,
 *  which then outlives the run that caused it. The bench starts from an open contact. */
async function quietContact(page) {
  const list = await cameras(page)
  const master = list.find((camera) => camera.name === MASTER)
  const ptz = list.find((camera) => camera.name === PTZ)
  await api(page, 'POST',
    `/api/DeviceIo/cameras/${master.cameraId}/relay-outputs/peer/state`, { active: false })
  const deadline = Date.now() + 60000
  while (Date.now() < deadline) {
    const rows = await api(page, 'GET',
      `/api/DeviceIo/cameras/${ptz.cameraId}/digital-inputs`) || []
    if ((rows.find((row) => row.token === 'input') || {}).liveActive === false) return
    await page.waitForTimeout(2000)
  }
  throw new Error('the simulated contact did not open')
}

async function setInputBehaviour(page, cameraName, { alarm = true, trigger = 'closed' }) {
  await page.getByText(cameraName, { exact: false }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('tab', { name: 'Devices', exact: true }).click()
  await page.waitForTimeout(2000)
  const alarmSwitch = page.getByLabel(/Alarm on contact/)
  if (alarm) await alarmSwitch.check()
  await page.getByLabel(/Trigger on/).click()
  await page.getByRole('option', { name: trigger === 'closed' ? 'Closed' : 'Open', exact: true }).click()
  // The card is the second Save inside the input row: the idle-state Save comes first.
  await page.getByRole('button', { name: /^save$/i }).nth(1).click()
  await page.waitForTimeout(1500)
}

/** The arming mode is the object's own: Admin → Nodes finds the part by its name. */
async function armInput(page, inputName) {
  await page.goto(`${BASE}/admin/nodes`, { waitUntil: 'domcontentloaded' })
  await page.getByPlaceholder(/Device or part name/).fill(inputName)
  await page.waitForTimeout(1500)
  await page.locator('[role="button"]').filter({ hasText: inputName }).first().click()
  await page.waitForTimeout(1000)
  await page.getByLabel('Arming', { exact: true }).click()
  await page.getByRole('option', { name: 'Armed', exact: true }).click()
  await page.getByRole('button', { name: /^save$/i }).first().click()
  await page.waitForTimeout(1500)
}

async function createMap(page) {
  await openMaps(page)
  await addMenuItem(page, 'New map')
  await page.getByLabel(/Name/).fill(MAP)
  await page.getByLabel(/Go to/).fill('38.9929, -76.9379')
  await page.keyboard.press('Enter')
  await page.waitForTimeout(1500)
  await applyAdd(page)
  await page.waitForTimeout(2500)
}

async function createPlan(page) {
  await openMaps(page, MAP)
  await addMenuItem(page, new RegExp(`^New plan inside ${MAP}$`))
  await page.getByLabel(/Name/).fill(PLAN)
  await page.getByLabel('Coordinates').click()
  await page.getByRole('option', { name: /Real/ }).click()
  await applyAdd(page)
  await page.waitForTimeout(2500)
}

async function uploadPlanPicture(page) {
  await openMaps(page, PLAN)
  await page.locator('input[type=file][accept*="svg"]').setInputFiles(path.join(FIXTURES, 'parity-plan.svg'))
  await page.getByRole('button', { name: /Replace plan|Upload plan/ }).first().waitFor({ timeout: 20000 })
  await page.waitForTimeout(1500)
  await shot(page, 'plan-picture.png')
}

async function drawFootprint(page) {
  await openMaps(page, PLAN)
  await page.getByRole('button', { name: "Draw the outline on the parent's map", exact: true }).click()
  await page.waitForTimeout(1500)
  for (const [fx, fy] of [[0.48, 0.47], [0.56, 0.47], [0.56, 0.55], [0.48, 0.55]]) {
    await leafletClick(page, fx, fy)
  }
  await page.keyboard.press('Enter')
  await page.waitForTimeout(2000)
}

async function freeFigures(page) {
  await openMaps(page, PLAN)
  await page.getByRole('button', { name: 'Place a text label', exact: true }).click()
  await leafletClick(page, 0.43, 0.50)
  await page.waitForTimeout(1200)
  await shot(page, 'text-figure.png')

  const chooser = page.waitForEvent('filechooser')
  await page.getByRole('button', { name: 'Place a picture', exact: true }).click()
  const fileChooser = await chooser
  await fileChooser.setFiles(path.join(FIXTURES, 'parity-icon.png'))
  await page.waitForTimeout(800)
  await leafletClick(page, 0.58, 0.50)
  await page.waitForTimeout(1200)
  await shot(page, 'picture-figure.png')

  await page.getByRole('button', { name: 'Draw a line', exact: true }).click()
  await leafletClick(page, 0.42, 0.58)
  await leafletClick(page, 0.6, 0.58)
  await page.keyboard.press('Enter')
  await page.waitForTimeout(1000)

  await page.getByRole('button', { name: 'Draw a polygon', exact: true }).click()
  await leafletClick(page, 0.44, 0.62)
  await leafletClick(page, 0.52, 0.62)
  await leafletClick(page, 0.48, 0.68)
  await page.keyboard.press('Enter')
  await page.waitForTimeout(1000)
  await shot(page, 'free-figures.png')
}

/** Landmarks are the survey a calibration is fitted to: four on one line cannot span a plane, and
 *  the fit (and the aiming that uses it) is refused. They are set out as a quad, not a row. */
const LANDMARK_SPOTS = [[0.40, 0.40], [0.58, 0.42], [0.42, 0.58], [0.60, 0.60]]

async function addLandmarks(page, count) {
  await page.getByRole('button', { name: 'Add landmarks', exact: true }).click()
  for (let index = 0; index < count; index += 1) {
    const [fx, fy] = LANDMARK_SPOTS[index % LANDMARK_SPOTS.length]
    await leafletClick(page, fx, fy)
  }
  await page.getByRole('button', { name: 'Select and move', exact: true }).click()
}

async function placeCamera(page, spaceName, cameraName, fx, fy) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Devices/ }).first().click()
  await page.waitForTimeout(800)
  await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${cameraName}`) }).first().click()
  await page.waitForTimeout(500)
  await leafletClick(page, fx, fy)
  await page.waitForTimeout(2000)
}

async function createDiagramType(page, { name, background, regions = [], device = false }) {
  await page.getByRole('button', { name: 'Types', exact: true }).click()
  await page.waitForTimeout(800)
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: /^add type$/i }).click()
  await page.waitForTimeout(500)
  // The dialog keeps every type as an accordion: the one just added is the last, and the older
  // ones stay in the DOM, collapsed.
  const type = dialog.locator('.MuiAccordion-root').last()
  await type.locator('.MuiAccordionSummary-root').click()
  await page.waitForTimeout(600)
  await type.getByLabel('Name', { exact: true }).fill(name)
  if (background) {
    await type.locator('input[type=file]').first().setInputFiles(path.join(FIXTURES, background))
    await page.waitForTimeout(1200)
  }
  if (device) {
    await type.getByLabel('Device type', { exact: true }).selectOption('camera')
    await type.getByLabel(/Manufacturer/).fill('OnvifSim')
    await type.getByLabel(/Model/).fill('Simulated fixed camera')
    await type.getByLabel(/Create it for a matching device/).check()
  }
  for (const region of regions) {
    await type.getByRole('button', { name: /ADD REGION/i }).click()
    await page.waitForTimeout(400)
    if (region.left) await type.getByLabel('Left', { exact: true }).fill(region.left)
    if (region.bind) {
      await type.getByRole('button', { name: 'STAND FOR A DEVICE PART' }).click()
      await page.waitForTimeout(300)
      await type.getByLabel('Part', { exact: true }).selectOption(region.bind.kind)
      await type.getByLabel('Find it by', { exact: true }).selectOption(region.bind.by)
      await type.getByLabel(/Token, name or number/).fill(region.bind.value)
    }
  }
  await dialog.getByRole('button', { name: /^save$/i }).click()
  await page.waitForTimeout(1500)
  await dialog.getByRole('button', { name: /^close$/i }).click()
  await page.waitForTimeout(500)
}

/** Renames the node whose element editor is open, keeping whatever type it already has. */
async function renameSelected(page, name) {
  await page.getByLabel('Name', { exact: true }).first().fill(name)
}

async function assignType(page, typeName) {
  await page.getByLabel('Type', { exact: true }).click()
  await page.getByRole('option', { name: typeName, exact: true }).click()
  await applyButton(page)
}

async function placeDiagram(page, spaceName, fx, fy) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('button', { name: 'New diagram on this canvas', exact: true }).click()
  await page.waitForTimeout(500)
  await leafletClick(page, fx, fy)
  await page.waitForTimeout(2000)
}

async function placeDeviceDiagram(page, spaceName, cameraName, fx, fy) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(1000)
  const row = page.locator('[role="button"]')
    .filter({ hasText: new RegExp(`^${cameraName}`) }).first()
  await row.getByRole('button', { name: 'Place the device diagram on this canvas' }).click()
  await page.waitForTimeout(500)
  await leafletClick(page, fx, fy)
  await page.waitForTimeout(3000)
}

async function openDiagram(page) {
  const row = page.locator('[role="button"]').filter({ hasText: /^Diagram 1/ }).first()
  await row.click()
  await page.getByTestId('diagram-paper').waitFor({ timeout: 20000 })
}

async function createNestedDiagram(page, name, regionFraction, typeName) {
  await page.getByRole('button', { name: 'New diagram', exact: true }).click()
  await page.waitForTimeout(2000)
  // The new diagram opens with its own root selected: name it (and type it) before going back.
  await renameSelected(page, name)
  if (typeName) {
    await page.getByLabel('Type', { exact: true }).click()
    await page.getByRole('option', { name: typeName, exact: true }).click()
  }
  await applyButton(page)

  await page.locator('button[aria-label="Up one level"]').click()
  await page.waitForTimeout(1500)
  const paper = await page.getByTestId('diagram-paper').boundingBox()
  const child = page.locator(`[title^="${name}"]`).first()
  const box = await child.boundingBox()
  const sx = box.x + box.width / 2
  const sy = box.y + box.height / 2
  const tx = paper.x + paper.width * regionFraction[0]
  const ty = paper.y + paper.height * regionFraction[1]
  await page.mouse.move(sx, sy)
  await page.mouse.down()
  for (let step = 1; step <= 10; step += 1) {
    await page.mouse.move(sx + (tx - sx) * step / 10, sy + (ty - sy) * step / 10)
  }
  await page.mouse.up()
  await page.waitForTimeout(2000)
}


async function setIconOnSelected(page) {
  await page.getByRole('tab', { name: 'Object', exact: true }).click()
  await page.waitForTimeout(700)
  const chooser = page.waitForEvent('filechooser')
  await page.getByRole('button', { name: /^icon$/i }).click()
  await (await chooser).setFiles(path.join(FIXTURES, 'parity-icon.png'))
  await page.waitForTimeout(800)
  await applyButton(page)
}

/** A diagram may carry an icon of its own on the canvas it stands on. */
async function setDiagramIcon(page, spaceName, name) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(800)
  await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${name}`) }).first().click()
  await page.waitForTimeout(800)
  await setIconOnSelected(page)
}

async function placePartReplica(page, spaceName, cameraName, partName, fx, fy) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Devices/ }).first().click()
  await page.waitForTimeout(800)
  const camera = page.locator('[role="button"]')
    .filter({ hasText: new RegExp(`^${cameraName}`) }).first()
  await camera.getByRole('button').first().click()
  await page.waitForTimeout(1000)
  await page.locator('[role="button"]')
    .filter({ hasText: new RegExp(`^${partName}`) }).first().click()
  await leafletClick(page, fx, fy)
  await page.waitForTimeout(1500)
  await page.getByRole('tab', { name: 'Object', exact: true }).click()
  await page.waitForTimeout(800)
  await page.getByLabel('Caption', { exact: true }).fill(partName)
  await applyButton(page)
}

/** A right is a checkbox on the subject's row; the role `user` is the one the demo uses. */
async function grantRight(page, spaceName, action) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: 'Rights', exact: true }).click()
  await page.waitForTimeout(5000)
  const headers = (await page.locator('thead th').allInnerTexts()).map((text) => text.trim().toLowerCase())
  const column = headers.indexOf(action.toLowerCase()) - 1
  const row = page.locator('tbody tr').filter({
    has: page.getByText('user', { exact: true }),
  }).first()
  const box = row.locator('input[type=checkbox]').nth(column)
  if (!(await box.isChecked())) await box.click({ force: true })
  await page.waitForTimeout(1500)
  const space = await spaceByName(page, spaceName)
  const granted = (await api(page, 'GET', `/api/map/rights?objectId=${space.id}`)).rights || []
  if (!granted.some((right) => right.subject === 'user' && right.action === action))
    throw new Error(`the ${action} right on ${spaceName} was not saved`)
}

async function addLevel(page) {
  await openMaps(page, MAP)
  await page.getByRole('tab', { name: 'Space', exact: true }).click()
  await page.getByRole('button', { name: /^dictionaries$/i }).click()
  await page.waitForTimeout(800)
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('tab', { name: 'Scale layers', exact: true }).click()
  await page.waitForTimeout(500)
  await dialog.getByRole('button', { name: /add layer/i }).click()
  await page.waitForTimeout(500)
  const inputs = dialog.locator('tbody tr').last().locator('input')
  await inputs.nth(0).fill(LEVEL)
  await inputs.nth(1).fill('1')
  await inputs.nth(2).fill('2')
  await dialog.getByRole('button', { name: /^save$/i }).click()
  await page.waitForTimeout(1000)
  await dialog.getByRole('button', { name: /^close$/i }).click()
}

/** The zoom layer lives on the figure; the tree row does not carry it, so it is read back from
 *  the canvas by the object's id. */
async function figureZoom(page, name) {
  const found = await api(page, 'GET', `/api/map/objects/by-name?name=${encodeURIComponent(name)}`)
  const marker = (found.markers || [])[0]
  if (!marker) return null
  const figures = (await api(page, 'POST', '/api/map/objects/by-ids', [marker.id])).figs || []
  return figures[0]?.zoomLevel ?? ''
}

/** A level that exists is not yet a level in use: it is assigned to an object on the map. */
async function assignLevel(page, cameraName) {
  // The vehicle's placement follows its telemetry, so the canvas is re-read between attempts; the
  // editor saves the figure it was handed, and a stale one would write the layer onto nothing.
  for (let attempt = 0; attempt < 2; attempt += 1) {
    await openMaps(page, MAP)
    await page.getByRole('tab', { name: /^Nodes/ }).first().click()
    await page.waitForTimeout(800)
    await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${cameraName}`) }).first().click()
    await page.waitForTimeout(800)
    await page.getByRole('tab', { name: 'Object', exact: true }).click()
    await page.waitForTimeout(800)
    await page.getByLabel('Scale layer', { exact: true }).click()
    await page.getByRole('option', { name: LEVEL, exact: true }).last().click()
    await page.waitForTimeout(400)
    await applyButton(page)

    const zoom = await figureZoom(page, `${cameraName} rep1`)
    if (zoom === LEVEL) return
    await page.waitForTimeout(1500)
  }
  throw new Error(`the scale layer ${LEVEL} was not saved on ${cameraName}`)
}

async function createWall(page) {
  await page.goto(`${BASE}/admin/monitors`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(2000)
  await page.locator('button[aria-label="New monitor"]').first().click()
  await page.waitForTimeout(800)
  await page.getByLabel(/Name/).fill(WALL)
  await page.getByRole('combobox').nth(0).click()
  await page.getByRole('option', { name: '2x2', exact: true }).click()
  await page.waitForTimeout(500)
  const names = [MASTER, PTZ, VEHICLE]
  for (let index = 0; index < names.length; index += 1) {
    await page.getByRole('combobox').nth(index + 1).click()
    await page.getByRole('option', { name: names[index], exact: true }).click()
    await page.waitForTimeout(300)
  }
  await page.getByRole('button', { name: 'Add', exact: true }).click()
  await page.waitForTimeout(2000)
}

async function createRouting(page) {
  const ptz = (await cameras(page)).find((camera) => camera.name === PTZ)
  const master = (await cameras(page)).find((camera) => camera.name === MASTER)
  await page.goto(`${BASE}/admin/event-routing`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(2000)
  await page.locator('button[aria-label="New subscription"]').first().click()
  await page.waitForTimeout(800)
  await page.getByLabel(/Name/).fill(RULE)
  await page.getByLabel('Event', { exact: true }).click()
  await page.getByRole('option', { name: /sensor/i }).click()
  await page.getByLabel('Source selector', { exact: true }).fill(JSON.stringify({
    camera_ids: [ptz.cameraId],
    tokens: ['input'],
    props: { contact: 'closed' },
  }))
  await page.getByLabel('Kind', { exact: true }).nth(0).click()
  await page.getByRole('option', { name: 'retain_recording', exact: true }).click()
  await page.getByLabel('parameters', { exact: true }).nth(0).fill(JSON.stringify({
    cameras: [master.cameraId, ptz.cameraId],
    timeout_seconds: 60,
    raise_alarm: false,
  }))
  await page.getByRole('button', { name: 'Add action', exact: true }).click()
  await page.waitForTimeout(400)
  await page.getByLabel('Kind', { exact: true }).nth(1).click()
  await page.getByRole('option', { name: 'set_relay', exact: true }).click()
  await page.getByLabel('parameters', { exact: true }).nth(1).fill(JSON.stringify({
    camera_id: master.cameraId,
    token: 'lamp',
    active: true,
  }))
  await page.getByRole('button', { name: 'Apply', exact: true }).click()
  await page.waitForTimeout(2000)
}

async function setBindingAndTracking(page) {
  await page.goto(`${BASE}/admin/cameras`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  await page.getByText(PTZ, { exact: false }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('tab', { name: 'Master', exact: true }).click()
  await page.waitForTimeout(1200)
  await page.getByRole('combobox').first().click()
  await page.getByRole('option', { name: MASTER, exact: true }).click()
  await page.waitForTimeout(1000)
  await page.getByRole('button', { name: 'Through the map', exact: true }).click()
  await page.waitForTimeout(1500)

  await page.getByRole('tab', { name: 'Tracking', exact: true }).click()
  await page.waitForTimeout(1000)
  await page.getByLabel(/Follow objects automatically/).check()
  // Kinds are a priority list: choose one from the "Add a kind" select rather than a checkbox.
  await page.getByText('Add a kind…').click()
  await page.getByRole('option', { name: 'Person', exact: true }).click()
  await page.waitForTimeout(400)
  const analytics = page.getByLabel(/^Analytics$/i)
  if (await analytics.count() && await analytics.isChecked()) await analytics.uncheck()
  const metadata = page.getByLabel(/camera metadata/i)
  if (await metadata.count() && !(await metadata.isChecked())) await metadata.check()
  await page.getByRole('button', { name: /^save$/i }).click()
  await page.waitForTimeout(1500)
}

async function calibrateGround(page, spaceName, cameraName) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(800)
  await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${cameraName}`) }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('tab', { name: 'Selection', exact: true }).click()
  await page.waitForTimeout(800)
  await page.getByRole('button', { name: /Calibrate by landmarks/ }).click()
  await page.waitForTimeout(8000)
  const dialog = page.getByRole('dialog')
  const video = dialog.locator('video').first()
  const box = await video.boundingBox()
  // Four marks on one diagonal cannot be fitted to a plane: the projection is refused and nothing
  // can be aimed. They are spread over the picture as a quad, which is what a person marking a
  // survey would do anyway.
  const marks = [[0.30, 0.30], [0.70, 0.30], [0.30, 0.70], [0.70, 0.70]]
  for (let index = 0; index < 4; index += 1) {
    const [mx, my] = marks[index]
    await dialog.locator('tbody tr').nth(index).click()
    await page.waitForTimeout(400)
    await page.mouse.click(box.x + box.width * mx, box.y + box.height * my)
    await page.waitForTimeout(1200)
  }
  await dialog.getByRole('button', { name: /^close$/i }).click()
  await page.waitForTimeout(800)
}

/** Aiming the simulated camera is what an operator does by hand with the pad: the pose is the
 *  device's, and everything the world keeps is still recorded through the dialog. */
async function simPtzTo(pan, tilt, zoom = 0) {
  const response = await fetch(`${SIM_URL}/api/cameras/${SIM_PTZ_CAMERA}/ptz/goto`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pan, tilt, zoom }),
  })
  if (!response.ok) throw new Error(`aiming the simulated PTZ failed: ${response.status}`)
  await new Promise((resolve) => setTimeout(resolve, 1500))
}

async function calibratePtz(page, spaceName, cameraName) {
  await openMaps(page, spaceName)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(800)
  await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${cameraName}`) }).first().click()
  await page.waitForTimeout(800)
  await page.getByRole('tab', { name: 'Selection', exact: true }).click()
  await page.waitForTimeout(800)
  await page.getByRole('button', { name: /Calibrate by landmarks/ }).click()
  await page.waitForTimeout(3000)
  const dialog = page.getByRole('dialog')
  // The slave's aim is fitted from where these landmarks stand, so a pair of them in one
  // direction cannot define it: every landmark on the canvas is marked.
  const rows = await dialog.locator('tbody tr').count()
  for (let index = 0; index < rows; index += 1) {
    const row = dialog.locator('tbody tr').nth(index)
    await row.click()
    await page.waitForTimeout(2500)
    // Each landmark is marked with the camera actually pointing somewhere else: four recordings of
    // one pose cannot tell the fit which way anything is, and the service refuses it by name.
    await simPtzTo(10 + index * 25, -6 - index * 6)
    await row.getByRole('button').nth(2).click()   // record pan and tilt
    await page.waitForTimeout(1500)
    await row.getByRole('button').nth(0).click()   // record the zoom it stands at
    await page.waitForTimeout(1200)
  }
  await dialog.getByRole('button', { name: /^close$/i }).click()
  await page.waitForTimeout(800)
}

async function continueWorld(page, only) {
  const steps = only ? new Set(only.split(',')) : null
  const run = (step) => !steps || steps.has(step)

  if (run('rights')) {
    await grantRight(page, MAP, 'view')
    await grantRight(page, PLAN, 'control')
    await shot(page, '07-rights-level.png')
  }

  if (run('calibrate')) {
    await calibrateGround(page, PLAN, MASTER)
    await calibratePtz(page, MAP, PTZ)
    await shot(page, '08-calibration.png')
  }

  if (run('replica')) await placePartReplica(page, MAP, MASTER, 'relay1', 0.45, 0.82)
  if (run('icon')) await setDiagramIcon(page, PLAN, `${PREFIX}-floor-plan`)
  if (run('contact')) await quietContact(page)
  if (run('level')) {
    if (!(await levels(page)).some((level) => level.zoomLevel === LEVEL)) await addLevel(page)
    await assignLevel(page, VEHICLE)
    await shot(page, '07b-level.png')
  }
  if (run('wall')) await createWall(page)
  if (run('routing')) await createRouting(page)
  if (run('binding')) await setBindingAndTracking(page)
  if (run('wall') || run('routing') || run('binding'))
    await shot(page, '09-wall-routing.png')
  console.log('\nthe parity world is built')
}

async function demo(page, install) {
  await page.goto(`${BASE}/admin/deployment`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  if (install) {
    await confirmOnce(page)
    await page.getByRole('button', { name: /INSTALL DEMO/i }).click()
    await page.waitForTimeout(8000)
  } else {
    const remove = page.getByRole('button', { name: /REMOVE DEMO/i })
    if (await remove.count()) {
      await confirmOnce(page)
      await remove.click()
      await page.waitForTimeout(8000)
    }
  }
  await page.getByRole('button', { name: /^close$/i }).click().catch(() => {})
}

async function deleteAllCameras(page) {
  await page.goto(`${BASE}/admin/cameras`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  for (let guard = 0; guard < 20; guard += 1) {
    const list = await cameras(page)
    if (!Array.isArray(list) || list.length === 0) return
    const camera = list[0]
    const row = page.locator('[role="button"]').filter({ hasText: new RegExp(`^${camera.name}`) }).first()
    await row.click()
    await page.waitForTimeout(600)
    await confirmOnce(page)
    await page.getByRole('button', { name: /^delete camera$/i }).click()
    await page.waitForTimeout(1200)
  }
}

async function preflight(page) {
  const status = await api(page, 'GET', '/api/map/admin/demo')
  check(status && status.installed === false,
    'the stack is running with the demo off', JSON.stringify(status))
  const existing = await spaceByName(page, MAP)
  check(!existing, 'the parity map does not exist yet')
}

async function build(page) {
  await demo(page, false)
  await deleteAllCameras(page)
  // The demo is removed before this runs: its world must not be what the parity snapshot reads.
  await preflight(page)
  await shot(page, '00-clean-bench.png')

  const master = await createCamera(page, { name: MASTER, port: ONVIF_PORT_BASE, telemetry: false })
  check(!!master, 'the master camera is created')
  await addStream(page, MASTER)
  const ptz = await createCamera(page, {
    name: PTZ, port: ONVIF_PORT_BASE + 1, telemetry: true,
  })
  check(!!ptz, 'the PTZ camera is created')
  await addStream(page, PTZ)
  const vehicle = await createCamera(page, {
    name: VEHICLE, port: ONVIF_PORT_BASE + 4, telemetry: false, geoFromDevice: true,
  })
  check(!!vehicle, 'the vehicle camera is created with position from the device')
  await addStream(page, VEHICLE)
  await shot(page, '01-cameras.png')

  await page.waitForTimeout(8000)
  await nameParts(page, MASTER, 'sensor1', 'relay')
  await nameParts(page, PTZ, 'sensor2', 'ptzrelay')
  await quietContact(page)
  await setInputBehaviour(page, PTZ, {})
  await setInputBehaviour(page, MASTER, {})

  await createMap(page)
  check(!!(await spaceByName(page, MAP)), 'the map is created')
  await createPlan(page)
  check(!!(await spaceByName(page, PLAN)), 'the plan is created')
  await uploadPlanPicture(page)
  await drawFootprint(page)
  await freeFigures(page)
  await addLandmarks(page, 4)
  await openMaps(page, MAP)
  await addLandmarks(page, 4)
  await shot(page, '02-plan-landmarks.png')

  await placeCamera(page, PLAN, MASTER, 0.50, 0.52)
  await placeCamera(page, MAP, PTZ, 0.55, 0.60)
  await placeCamera(page, MAP, VEHICLE, 0.65, 0.62)
  await armInput(page, 'sensor2')
  await shot(page, '03-placements.png')

  await placeDiagram(page, PLAN, 0.45, 0.75)
  await openDiagram(page)
  await renameSelected(page, `${PREFIX}-floor-plan`)
  await applyButton(page)
  await createDiagramType(page, {
    name: `${PREFIX}-floor`, background: 'parity-layout.svg',
    regions: [{}, { left: '0.5' }],
  })
  await assignType(page, `${PREFIX}-floor`)
  await createDiagramType(page, {
    name: `${PREFIX}-port`, background: 'parity-icon.png',
  })
  await createNestedDiagram(page, `${PREFIX}-rack`, [0.2, 0.15], `${PREFIX}-floor`)
  await createNestedDiagram(page, `${PREFIX}-switch`, [0.6, 0.15], null)
  await shot(page, '04-diagrams.png')

  await createDiagramType(page, {
    name: `${PREFIX}-camera-panel`, background: 'parity-layout.svg',
    regions: [
      { bind: { kind: 'digital_input', by: 'token', value: 'input' } },
      { left: '0.5', bind: { kind: 'relay', by: 'token', value: 'lamp' } },
    ],
    device: true,
  })
  await placeDeviceDiagram(page, PLAN, MASTER, 0.58, 0.55)
  await shot(page, '05-device-diagram.png')

  // The camera's picture carries an icon; the same path draws the floor diagram's marker.
  await openMaps(page, PLAN)
  await page.getByRole('tab', { name: /^Nodes/ }).first().click()
  await page.waitForTimeout(800)
  await page.locator('[role="button"]').filter({ hasText: new RegExp(`^${MASTER}`) }).first().click()
  await page.waitForTimeout(800)
  await setIconOnSelected(page)
  await setDiagramIcon(page, PLAN, `${PREFIX}-floor-plan`)

  await placePartReplica(page, MAP, MASTER, 'relay1', 0.45, 0.82)
  await shot(page, '06-part-replica.png')

  await addLevel(page)
  await assignLevel(page, VEHICLE)
  // The icons, the replica and the level are already part of the build; the tail adds what only
  // makes sense on a finished world.
  await continueWorld(page, 'rights,calibrate,wall,routing,binding')
}

/** Drops the two databases the world lives in and recreates them empty.
 *
 *  The ordinary cleanup leaves two kinds of residue that no API can remove: plan and icon files
 *  whose DELETE endpoints do not exist, and anything an interrupted run left half-written. This is
 *  the way to a bench that is clean by construction -- and it is destructive, so it is guarded:
 *  --force must be given, the postgres has to be this checkout's own compose service, and no
 *  installation (a treealarm compose project) may be running. */
function resetDb() {
  if (!process.argv.includes('--force'))
    throw new Error('--reset-db drops ta_map and ta_cfg: pass --force to confirm')

  const docker = (args) => execFileSync('docker', args, { cwd: REPO, encoding: 'utf8' }).trim()
  const id = docker(['compose', 'ps', '-q', 'postgres'])
  if (!id) throw new Error("the dev stack is not running: this checkout's postgres is not up")

  const owner = docker([
    'inspect', id, '--format', '{{index .Config.Labels "com.docker.compose.project.working_dir"}}'])
  if (path.resolve(owner) !== REPO)
    throw new Error(`postgres belongs to ${owner}, not to this dev checkout`)

  const projects = docker(['ps', '--format', '{{.Label "com.docker.compose.project.working_dir"}}'])
    .split('\n').filter(Boolean)
  if (projects.some((dir) => dir.includes('ta_install')))
    throw new Error('an installation is running from ta_install: refusing to drop the bench')

  for (const database of ['ta_map', 'ta_cfg']) {
    for (const sql of [`DROP DATABASE IF EXISTS ${database} WITH (FORCE)`,
                       `CREATE DATABASE ${database}`]) {
      docker(['compose', 'exec', '-T', 'postgres', 'psql', '-U', 'postgres', '-d', 'postgres',
        '-c', sql])
    }
  }
  console.log('ta_map and ta_cfg were dropped and recreated. Restart the stack so the services run '
    + 'their migrations: scripts/dev_stack.py restart vmscfg vmsmap')
}

async function cleanup(page) {  // Things that are not part of the map tree go first: the dialogs that remove them need a map
  // open, and the map goes last.
  await page.goto(`${BASE}/admin/monitors`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  const wall = page.locator('[role="button"]').filter({ hasText: new RegExp(`^${WALL}`) }).first()
  if (await wall.count()) {
    await wall.click()
    await page.waitForTimeout(800)
    await page.getByRole('button', { name: /^delete monitor$/i }).click()
    await page.waitForTimeout(1500)
  }

  await page.goto(`${BASE}/admin/event-routing`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1500)
  const rule = page.locator('[role="button"]').filter({ hasText: new RegExp(`^${RULE}`) }).first()
  if (await rule.count()) {
    await rule.click()
    await page.waitForTimeout(800)
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await page.waitForTimeout(1500)
  }

  const root = await spaceByName(page, MAP)
  if (root) {
    await openMaps(page, MAP)
    await page.getByRole('tab', { name: 'Space', exact: true }).click()
    await page.getByRole('button', { name: /^dictionaries$/i }).click()
    await page.waitForTimeout(800)
    const dialog = page.getByRole('dialog')
    await dialog.getByRole('tab', { name: 'Scale layers', exact: true }).click()
    await page.waitForTimeout(500)
    const rows = dialog.locator('tbody tr')
    let found = null
    for (let index = 0; index < await rows.count(); index += 1) {
      const input = rows.nth(index).locator('input').first()
      if ((await input.inputValue().catch(() => '')) === LEVEL) {
        found = rows.nth(index)
        break
      }
    }
    if (found) {
      await found.getByRole('button').click()
      await dialog.getByRole('button', { name: /^save$/i }).click()
      await page.waitForTimeout(1000)
    }
    await dialog.getByRole('button', { name: /^close$/i }).click()
    await page.waitForTimeout(800)
  }
  if (root) {
    await page.getByText(MAP, { exact: true }).first().click()
    await page.waitForTimeout(800)
    await confirmOnce(page)
    await page.getByRole('button', { name: 'Delete', exact: true }).first().click()
    await page.waitForTimeout(2500)
  }
  await page.goto(`${BASE}/admin/cameras`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(1000)
  for (const name of [MASTER, PTZ, VEHICLE]) await deleteCamera(page, name)

  // Types are a dictionary, not part of the map: deleting the map does not take them with it.
  const types = (await api(page, 'POST', '/api/map/diagrams/types', {})).diagramTypes || []
  const ids = types.filter((type) => (type.name || '').startsWith(`${PREFIX}-`)).map((type) => type.id)
  if (ids.length) await api(page, 'DELETE', '/api/map/admin/diagram-types', { ids })
  await shot(page, '99-cleanup.png')
}

;(async () => {
  require('fs').mkdirSync(SHOTS, { recursive: true })
  if (process.argv.includes('--reset-db')) {
    resetDb()
    return
  }
  const phase = process.argv.includes('--phase')
    ? process.argv[process.argv.indexOf('--phase') + 1]
    : 'build'
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } })
  page.setDefaultTimeout(30000)
  page.on('dialog', (dialog) => { dialog.accept().catch(() => {}) })
  try {
    await login(page)
    check(true, 'the browser signs in')
    if (phase === 'demo') {
      await demo(page, true)
      await shot(page, 'demo-installed.png')
    } else if (phase === 'continue') {
      // --steps lets a half-built world pick up where the last run stopped.
      const at = process.argv.indexOf('--steps')
      await continueWorld(page, at > 0 ? process.argv[at + 1] : null)
    } else if (phase === 'cleanup') {
      await cleanup(page)
    } else {
      await build(page)
    }
  } finally {
    await browser.close()
  }
  const failed = results.filter((result) => !result.ok)
  console.log(`\n${results.length - failed.length}/${results.length} passed`)
  if (failed.length) {
    console.log('Screenshots: ' + SHOTS)
    process.exit(1)
  }
})().catch((error) => {
  console.error(error)
  process.exit(1)
})

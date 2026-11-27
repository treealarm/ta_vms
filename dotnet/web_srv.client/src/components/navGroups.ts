/** Navigation descriptors, kept apart from the views so the slugs can be checked without a DOM. */
export type NavItem = { slug: string; labelKey: string }
export type NavGroup = { key: string; titleKey: string; items: NavItem[] }

// Slugs are route segments and therefore deep links — grouping and labels change, these do not.
export const ADMIN_NAV_GROUPS: NavGroup[] = [
  {
    key: 'devices',
    titleKey: 'nav.groups.devices',
    items: [
      { slug: 'cameras', labelKey: 'nav.cameras' },
      { slug: 'onvif-settings', labelKey: 'nav.onvifConfiguration' },
      { slug: 'mediamtx', labelKey: 'nav.mediaMtx' },
    ],
  },
  {
    key: 'space',
    titleKey: 'nav.groups.space',
    items: [
      { slug: 'maps', labelKey: 'nav.maps' },
      { slug: 'nodes', labelKey: 'nav.nodes' },
    ],
  },
  {
    key: 'video',
    titleKey: 'nav.groups.video',
    items: [{ slug: 'monitors', labelKey: 'nav.videoWalls' }],
  },
  {
    key: 'recording-storage',
    titleKey: 'nav.groups.recordingStorage',
    items: [
      { slug: 'storages', labelKey: 'nav.storages' },
      { slug: 'recording', labelKey: 'nav.recordingProfiles' },
      { slug: 'schedules', labelKey: 'nav.recordingSchedules' },
      { slug: 'edge-archive', labelKey: 'nav.edgeArchive' },
    ],
  },
  {
    key: 'events-analytics',
    titleKey: 'nav.groups.eventsAnalytics',
    items: [
      { slug: 'onvif', labelKey: 'nav.onvif' },
      { slug: 'event-routing', labelKey: 'nav.eventRouting' },
      { slug: 'analytics', labelKey: 'nav.analytics' },
    ],
  },
  {
    key: 'system',
    titleKey: 'nav.groups.system',
    items: [{ slug: 'deployment', labelKey: 'nav.deployment' }],
  },
]

export const OPERATOR_NAV_GROUPS: NavGroup[] = [
  {
    key: 'live',
    titleKey: 'nav.groups.live',
    items: [
      { slug: 'monitors', labelKey: 'nav.monitors' },
      { slug: 'investigations', labelKey: 'nav.investigations' },
    ],
  },
  {
    key: 'map',
    titleKey: 'nav.groups.map',
    items: [
      { slug: 'map', labelKey: 'nav.map' },
      { slug: 'control', labelKey: 'nav.control' },
    ],
  },
  {
    key: 'events-archive',
    titleKey: 'nav.groups.eventsArchive',
    items: [
      { slug: 'events', labelKey: 'nav.events' },
      { slug: 'object-findings', labelKey: 'nav.objectFindings' },
      { slug: 'gaps', labelKey: 'nav.gaps' },
    ],
  },
]

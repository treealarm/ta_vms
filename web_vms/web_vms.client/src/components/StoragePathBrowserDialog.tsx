import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  List,
  ListItemButton,
  ListItemText,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import FolderIcon from '@mui/icons-material/Folder'
import StorageIcon from '@mui/icons-material/Storage'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { DirectoryListingDto } from '../dto/dtos'
import { formatBytes } from './edgeArchiveModel'

type Props = {
  open: boolean
  onClose: () => void
  /** Called with an absolute path on the browsed node — stored verbatim, never rewritten. */
  onPick: (path: string) => void
}

/**
 * Picks a storage root by browsing the disk of a recording node.
 *
 * Only directories the node's file service just proved it can write into are listed, and browsing
 * always happens on one specific node: the paths are that machine's, in that machine's path style,
 * and mean nothing on any other.
 */
export default function StoragePathBrowserDialog({ open, onClose, onPick }: Props) {
  const { t } = useTranslation()
  const servers = useAppSelector((state) => state.live.servers)

  const [serverId, setServerId] = useState('')
  // '' is the volume-roots listing, not a path — the node decides what its roots are.
  const [path, setPath] = useState('')
  const [listing, setListing] = useState<DirectoryListingDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    setPath('')
    setListing(null)
    setError('')
  }, [open])

  // Kept apart from the reset above so a servers list that arrives late still gets a default,
  // without discarding the browsing the operator has already done.
  useEffect(() => {
    if (!open) return
    setServerId((prev) => prev || servers[0]?.serverId || '')
  }, [open, servers])

  const load = useCallback(async (targetServerId: string, targetPath: string) => {
    setLoading(true)
    setError('')
    try {
      const response = await fetch(
        `/api/Storage/browse?serverId=${encodeURIComponent(targetServerId)}`
        + `&path=${encodeURIComponent(targetPath)}`)
      if (!response.ok) {
        const text = await response.text()
        let message = text
        try { message = (JSON.parse(text) as { error?: string }).error ?? text } catch { /* raw text */ }
        throw new Error(message)
      }
      setListing(await response.json() as DirectoryListingDto)
    } catch (e) {
      setListing(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!open || !serverId) return
    void load(serverId, path)
  }, [open, serverId, path, load])

  const atRoots = path === ''
  const entries = listing?.entries ?? []

  const handleServerChange = (nextServerId: string) => {
    // Paths of one node say nothing about another's disk, so the browse restarts at its roots.
    setServerId(nextServerId)
    setPath('')
  }

  const handlePick = (picked: string) => {
    onPick(picked)
    onClose()
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>{t('storage.browseTitle')}</DialogTitle>
      <DialogContent>
        <Stack spacing={1.5} sx={{ mt: 1 }}>
          {servers.length > 1 && (
            <TextField
              select
              label={t('cameras.server')}
              value={serverId}
              onChange={(e) => handleServerChange(e.target.value)}
              size="small"
              sx={{ maxWidth: 240 }}
            >
              {servers.map((s) => (
                <MenuItem key={s.serverId} value={s.serverId}>{s.serverId}</MenuItem>
              ))}
            </TextField>
          )}

          <Stack direction="row" spacing={1} alignItems="center">
            <Tooltip title={t('storage.browseUp')}>
              <span>
                <IconButton
                  size="small"
                  disabled={atRoots || loading}
                  onClick={() => setPath(listing?.parentPath ?? '')}
                >
                  <ArrowUpwardIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
            <Typography variant="body2" sx={{ wordBreak: 'break-all' }}>
              {atRoots ? t('storage.browseVolumes') : path}
            </Typography>
            {loading && <CircularProgress size={16} />}
          </Stack>

          {error && <Typography variant="body2" color="error">{error}</Typography>}

          {!loading && !error && !serverId && (
            <Typography variant="body2" color="text.secondary">{t('storage.browseNoServer')}</Typography>
          )}

          {!loading && !error && listing && entries.length === 0 && (
            <Typography variant="body2" color="text.secondary">
              {listing.message || (atRoots ? t('storage.browseNoVolumes') : t('storage.browseEmpty'))}
            </Typography>
          )}

          {entries.length > 0 && (
            <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, maxHeight: 340, overflowY: 'auto' }}>
              <List dense disablePadding>
                {entries.map((entry) => (
                  <ListItemButton
                    key={entry.path}
                    onClick={() => setPath(entry.path)}
                    sx={{ gap: 1 }}
                  >
                    {atRoots ? <StorageIcon fontSize="small" color="action" /> : <FolderIcon fontSize="small" color="action" />}
                    <ListItemText
                      primary={entry.name}
                      // A directory that cannot be written into is still worth opening: what the
                      // operator is looking for is usually one level inside it.
                      secondary={entry.writable
                        ? (entry.totalBytes > 0
                            ? t('storage.browseSpace', {
                                free: formatBytes(entry.freeBytes),
                                total: formatBytes(entry.totalBytes),
                              })
                            : t('storage.browseSpaceUnknown'))
                        : t('storage.browseNotWritable')}
                      slotProps={{
                        primary: { sx: { wordBreak: 'break-all' } },
                        secondary: entry.writable ? undefined : { color: 'text.disabled' },
                      }}
                      sx={{ opacity: entry.writable ? 1 : 0.6 }}
                    />
                    <Button
                      size="small"
                      disabled={!entry.writable}
                      onClick={(e) => { e.stopPropagation(); handlePick(entry.path) }}
                    >
                      {t('storage.browseSelect')}
                    </Button>
                  </ListItemButton>
                ))}
              </List>
            </Box>
          )}

          {/* Truncation and other partial-answer notes, when there is still a list to qualify. */}
          {listing && listing.message && entries.length > 0 && (
            <Typography variant="caption" color="text.secondary">{listing.message}</Typography>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button
          variant="contained"
          disabled={atRoots || !listing?.writable}
          onClick={() => handlePick(path)}
        >
          {t('storage.browseSelectCurrent')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

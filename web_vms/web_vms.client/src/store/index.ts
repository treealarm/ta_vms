import { configureStore } from '@reduxjs/toolkit'
import { useDispatch, useSelector, type TypedUseSelectorHook } from 'react-redux'
import authReducer from './authSlice'
import liveReducer from './liveSlice'
import fsReducer from './fsSlice'
import logsReducer from './logSlice'
import entityStateReducer from './entityStateSlice'
import storageReducer from './storageSlice'
import mediaMtxReducer from './mediaMtxSlice'
import monitorsReducer from './monitorsSlice'
import monitorUiReducer from './monitorUiSlice'
import wallPlaybackReducer from './wallPlaybackSlice'
import wallCoverageReducer from './wallCoverageSlice'
import frontendConfigReducer from './frontendConfigSlice'

export const store = configureStore({
  reducer: {
    auth: authReducer,
    live: liveReducer,
    fs: fsReducer,
    logs: logsReducer,
    entityState: entityStateReducer,
    storage: storageReducer,
    mediaMtx: mediaMtxReducer,
    monitors: monitorsReducer,
    monitorUi: monitorUiReducer,
    wallPlayback: wallPlaybackReducer,
    wallCoverage: wallCoverageReducer,
    frontendConfig: frontendConfigReducer,
  }
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch

export const useAppDispatch = () => useDispatch<AppDispatch>()
export const useAppSelector: TypedUseSelectorHook<RootState> = useSelector

/** Returns the camera's display name, falling back to cameraId when name is absent. */
export function useCameraName(cameraId: string | null | undefined): string {
  return useAppSelector((s) =>
    cameraId ? (s.live.cameraStatesById[cameraId]?.name || cameraId) : ''
  )
}

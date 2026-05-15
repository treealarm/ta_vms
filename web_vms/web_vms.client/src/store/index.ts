import { configureStore } from '@reduxjs/toolkit'
import { useDispatch, useSelector, type TypedUseSelectorHook } from 'react-redux'
import liveReducer from './liveSlice'
import fsReducer from './fsSlice'
import logsReducer from './logSlice'
import storageReducer from './storageSlice'
import mediaMtxReducer from './mediaMtxSlice'
import monitorsReducer from './monitorsSlice'
import monitorUiReducer from './monitorUiSlice'

export const store = configureStore({
  reducer: {
    live: liveReducer,
    fs: fsReducer,
    logs: logsReducer,
    storage: storageReducer,
    mediaMtx: mediaMtxReducer,
    monitors: monitorsReducer,
    monitorUi: monitorUiReducer,
  }
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch

export const useAppDispatch = () => useDispatch<AppDispatch>()
export const useAppSelector: TypedUseSelectorHook<RootState> = useSelector

import { configureStore } from '@reduxjs/toolkit'
import { useDispatch, useSelector, type TypedUseSelectorHook } from 'react-redux'
import liveReducer from './liveSlice'
import archReducer from './archSlice'
import fsReducer from './fsSlice'

export const store = configureStore({
  reducer: {
    live: liveReducer,
    arch: archReducer,
    fs: fsReducer
  }
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch

export const useAppDispatch = () => useDispatch<AppDispatch>()
export const useAppSelector: TypedUseSelectorHook<RootState> = useSelector

import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAppSelector } from '../store'
import { decodeRoles } from '../store/authSlice'

interface RequireRoleProps {
  role?: string
  children: ReactNode
}

/**
 * Gates a route by Keycloak role. With no token (standalone/not-logged-in-yet) it renders
 * children unconditionally — if the backend actually requires auth, the page's own API calls
 * will 401 and authFetch redirects to /login. This keeps standalone (no Keycloak) behavior
 * unchanged while still protecting admin routes once a session exists.
 */
export default function RequireRole({ role, children }: RequireRoleProps) {
  const token = useAppSelector((s) => s.auth.token)

  if (token && role && !decodeRoles(token).includes(role)) {
    return <Navigate to="/operator/monitors" replace />
  }

  return <>{children}</>
}

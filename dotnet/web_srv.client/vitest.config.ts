import { defineConfig } from 'vitest/config'

// Tests run over plain TypeScript — no DOM, no dev server. The app's own vite.config.ts carries
// the react refresh and the type checker, neither of which a unit test needs.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})

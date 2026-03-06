import { defineConfig, devices } from '@playwright/test';

/**
 * DriveChill E2E test configuration.
 *
 * Prerequisites before running:
 *   1. Start the mock backend on port 8085:
 *      cd ../backend && DRIVECHILL_HARDWARE_BACKEND=mock python drivechill.py --headless
 *   2. Run tests:
 *      npm run test:e2e
 *
 * The webServer block below starts the Next.js dev server automatically.
 * It points at the mock backend via NEXT_PUBLIC_API_URL.
 *
 * Override the base URL for production builds:
 *   PLAYWRIGHT_BASE_URL=http://localhost:8085 npm run test:e2e
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:3000',
    // Capture trace on first retry to ease debugging.
    trace: 'on-first-retry',
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  // Start the Next.js dev server pointed at the mock backend.
  // Set PLAYWRIGHT_BASE_URL to skip this if testing against an already-running server.
  webServer: process.env.PLAYWRIGHT_BASE_URL ? undefined : {
    command: 'cross-env NEXT_PUBLIC_API_URL=http://localhost:8085 NEXT_PUBLIC_WS_URL=ws://localhost:8085/api/ws npm run dev',
    url: 'http://localhost:3000',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});

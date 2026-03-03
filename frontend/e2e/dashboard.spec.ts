import { test, expect } from '@playwright/test';

test.describe('Dashboard', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    // Wait for the app to finish its auth/session check
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });
  });

  test('loads the dashboard page', async ({ page }) => {
    // The main content area should be present
    await expect(page.locator('main')).toBeVisible();
  });

  test('shows temperature sensor cards', async ({ page }) => {
    // Temperature cards contain °C or °F values — look for a degree symbol
    const degreeText = page.getByText(/°[CF]/, { exact: false });
    await expect(degreeText.first()).toBeVisible({ timeout: 10_000 });
  });

  test('shows fan speed cards', async ({ page }) => {
    // Fan cards show RPM values
    const rpmText = page.getByText(/RPM/i, { exact: false });
    await expect(rpmText.first()).toBeVisible({ timeout: 10_000 });
  });

  test('shows connection status indicator', async ({ page }) => {
    // Either "Connected" or "Disconnected" should appear in the header area
    const status = page.getByText(/Connected|Disconnected/i);
    await expect(status.first()).toBeVisible({ timeout: 10_000 });
  });

  test('navigation sidebar is visible on desktop', async ({ page }) => {
    // The sidebar navigation links should be present
    await expect(page.getByRole('link', { name: /dashboard/i }).or(
      page.getByRole('button', { name: /dashboard/i })
    ).first()).toBeVisible();
  });
});

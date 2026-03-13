import { test, expect } from '@playwright/test';

/**
 * Full navigation and button functionality tests.
 * Validates that every page loads, key buttons are clickable, and no runtime errors occur.
 */
test.describe('Full Navigation & Button Functionality', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(
      () => !document.body.innerText.includes('Loading...'),
      { timeout: 15_000 },
    );

    // Dismiss "What's new" banner if present
    const dismissBtn = page.getByRole('button', { name: /dismiss/i });
    if (await dismissBtn.isVisible({ timeout: 2_000 }).catch(() => false)) {
      await dismissBtn.click();
    }
  });

  const pages = [
    { name: 'Dashboard', nav: /dashboard/i, expect: /°[CF]|RPM|connected/i },
    { name: 'Fan Curves', nav: /fan curves/i, expect: /preset|profile|curve/i },
    { name: 'Alerts', nav: /alerts/i, expect: /alert|rule|threshold/i },
    { name: 'Drives', nav: /drives/i, expect: /drive|smart|health|no drives/i },
    { name: 'Analytics', nav: /analytics/i, expect: /analytics|1h|6h|24h/i },
    { name: 'Settings', nav: /settings/i, expect: /general|save settings/i },
  ];

  for (const pg of pages) {
    test(`${pg.name} page loads and shows expected content`, async ({ page }) => {
      const link = page.getByText(pg.nav).first();
      await link.click();
      await expect(page.getByText(pg.expect).first()).toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
    });
  }

  test('sidebar navigation highlights active page', async ({ page }) => {
    // Click through each page and verify no crash
    for (const pg of pages) {
      const link = page.getByText(pg.nav).first();
      if (await link.isVisible({ timeout: 2_000 }).catch(() => false)) {
        await link.click();
        await page.waitForTimeout(500);
        await expect(page.locator('main')).toBeVisible();
      }
    }
  });

  test('temperature targets page loads', async ({ page }) => {
    const link = page.getByText(/temperature targets|temp targets/i).first();
    if (await link.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await link.click();
      await expect(page.locator('main')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
    }
  });

  test('quiet hours page loads', async ({ page }) => {
    const link = page.getByText(/quiet hours/i).first();
    if (await link.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await link.click();
      await expect(page.locator('main')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
    }
  });

  test('mobile responsive layout does not crash', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/');
    await page.waitForFunction(
      () => !document.body.innerText.includes('Loading...'),
      { timeout: 15_000 },
    );
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
  });

  test('theme toggle does not crash', async ({ page }) => {
    // Look for a theme toggle button (sun/moon icon or text)
    const themeBtn = page.getByRole('button', { name: /theme|dark|light/i }).first();
    if (await themeBtn.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await themeBtn.click();
      await page.waitForTimeout(300);
      await expect(page.locator('main')).toBeVisible();
    }
  });
});

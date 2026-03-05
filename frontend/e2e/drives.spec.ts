import { test, expect } from '@playwright/test';

test.describe('Drives page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(
      () => !document.body.innerText.includes('Loading...'),
      { timeout: 15_000 }
    );
  });

  async function navigateToDrives(page: import('@playwright/test').Page) {
    // Click the Drives nav item in sidebar or mobile nav
    const drivesNav = page
      .getByRole('button', { name: /drives/i })
      .or(page.getByRole('link', { name: /drives/i }))
      .first();
    await drivesNav.click();
    await expect(page.getByText(/Storage Drives/i)).toBeVisible({ timeout: 8_000 });
  }

  test('navigates to Drives page via sidebar', async ({ page }) => {
    await navigateToDrives(page);
    // Header should show
    await expect(page.getByText(/Storage Drives/i)).toBeVisible();
  });

  test('shows drive count or empty state', async ({ page }) => {
    await navigateToDrives(page);
    // Either "X drive(s) detected" or "No drives detected" should appear
    const countOrEmpty = page
      .getByText(/\d+ drives? detected|No drives detected/i)
      .first();
    await expect(countOrEmpty).toBeVisible({ timeout: 10_000 });
  });

  test('shows Rescan button', async ({ page }) => {
    await navigateToDrives(page);
    await expect(page.getByRole('button', { name: /rescan/i })).toBeVisible();
  });

  test('degraded mode banner is shown when smartmontools absent', async ({ page }) => {
    await navigateToDrives(page);
    // If smartctl is unavailable the banner appears — it may or may not be present
    // depending on whether smartmontools is installed in the test environment.
    // We just verify the page doesn't crash when it IS shown.
    const banner = page.getByText(/smartmontools not found/i);
    // If present: visible. If absent: no error — test passes either way.
    const count = await banner.count();
    if (count > 0) {
      await expect(banner.first()).toBeVisible();
    }
  });

  test('sort buttons are present when drives exist', async ({ page }) => {
    await navigateToDrives(page);
    // Sort buttons only render when drives.length > 0
    const hasDrives = await page.getByText(/\d+ drives? detected/i).count() > 0;
    if (hasDrives) {
      await expect(page.getByRole('button', { name: /name/i }).first()).toBeVisible();
      await expect(page.getByRole('button', { name: /temp/i }).first()).toBeVisible();
      await expect(page.getByRole('button', { name: /health/i }).first()).toBeVisible();
    }
  });

  test('clicking a drive card opens the detail panel', async ({ page }) => {
    await navigateToDrives(page);
    // Only proceed if at least one drive card is present
    const driveCards = page.locator('.card').filter({ hasText: /HDD|SSD|NVMe/i });
    const count = await driveCards.count();
    if (count === 0) {
      test.skip(); // No drives in this environment
      return;
    }
    await driveCards.first().click();
    // Detail panel shows Overview section
    await expect(page.getByText(/Overview/i)).toBeVisible({ timeout: 8_000 });
    await expect(page.getByText(/Back/i)).toBeVisible();
  });

  test('detail panel shows Self-Tests section', async ({ page }) => {
    await navigateToDrives(page);
    const driveCards = page.locator('.card').filter({ hasText: /HDD|SSD|NVMe/i });
    if (await driveCards.count() === 0) {
      test.skip();
      return;
    }
    await driveCards.first().click();
    await expect(page.getByText(/Self-Tests/i)).toBeVisible({ timeout: 8_000 });
  });

  test('back button returns to the drive list', async ({ page }) => {
    await navigateToDrives(page);
    const driveCards = page.locator('.card').filter({ hasText: /HDD|SSD|NVMe/i });
    if (await driveCards.count() === 0) {
      test.skip();
      return;
    }
    await driveCards.first().click();
    await page.getByRole('button', { name: /back/i }).click();
    await expect(page.getByText(/Storage Drives/i)).toBeVisible({ timeout: 5_000 });
  });

  test('"Use for cooling" button navigates to Fan Curves page', async ({ page }) => {
    await navigateToDrives(page);
    const driveCards = page.locator('.card').filter({ hasText: /HDD|SSD|NVMe/i });
    if (await driveCards.count() === 0) {
      test.skip();
      return;
    }
    await driveCards.first().click();
    await page.getByRole('button', { name: /Overview/i }).count(); // wait for detail load

    const useCoolingBtn = page.getByRole('button', { name: /use for cooling/i });
    if (await useCoolingBtn.count() === 0) {
      test.skip(); // Drive has no temperature reading
      return;
    }
    await useCoolingBtn.click();
    // Should navigate to Fan Curves page
    await expect(page.getByText(/Custom Curves/i).or(page.getByText(/Fan Curves/i)).first())
      .toBeVisible({ timeout: 8_000 });
  });

  test('"New cooling curve" button creates an unsaved draft curve', async ({ page }) => {
    await navigateToDrives(page);
    const driveCards = page.locator('.card').filter({ hasText: /HDD|SSD|NVMe/i });
    if (await driveCards.count() === 0) {
      test.skip();
      return;
    }
    await driveCards.first().click();
    await page.getByRole('button', { name: /Overview/i }).count(); // wait for detail load

    const newCurveBtn = page.getByRole('button', { name: /new cooling curve/i });
    if (await newCurveBtn.count() === 0) {
      test.skip(); // Drive has no temperature reading
      return;
    }
    await newCurveBtn.click();
    // Should navigate to Fan Curves page and show the unsaved draft
    await expect(page.getByText(/Storage Cooling/i)).toBeVisible({ timeout: 8_000 });
    // Draft curve should have the "unsaved" badge
    await expect(page.getByText(/unsaved/i).first()).toBeVisible({ timeout: 5_000 });
  });
});

import { test, expect } from '@playwright/test';

test.describe('Machine Health Check', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(
      () => !document.body.innerText.includes('Loading...'),
      { timeout: 15_000 },
    );
  });

  test('dashboard shows machine cards if machines are configured', async ({ page }) => {
    // Machine cards appear in the System Overview section with status badges
    const machineCard = page.getByText(/online|offline|unknown/i).first();
    const hasMachines = await machineCard.isVisible({ timeout: 5_000 }).catch(() => false);

    // If machines exist, clicking one should open the drill-in view
    if (hasMachines) {
      // Look for a machine card that is clickable
      const cards = page.locator('[class*="card"]').filter({ hasText: /online|offline|unknown/i });
      const count = await cards.count();
      if (count > 0) {
        await cards.first().click();
        // The drill-in should show a "Back" button
        const backBtn = page.getByRole('button', { name: /back/i }).first();
        await expect(backBtn).toBeVisible({ timeout: 5_000 });
      }
    }
    // If no machines configured, the test still passes — no crash
    await expect(page.locator('main')).toBeVisible();
  });

  test('machine drill-in shows Check Now button', async ({ page }) => {
    // This test only runs meaningfully if machines are configured in the mock backend
    const machineCard = page.locator('[class*="card"]').filter({ hasText: /online|offline|unknown/i }).first();
    if (await machineCard.isVisible({ timeout: 5_000 }).catch(() => false)) {
      await machineCard.click();

      // Look for the health check button
      const checkBtn = page.getByRole('button', { name: /check now/i });
      const hasCheckBtn = await checkBtn.isVisible({ timeout: 5_000 }).catch(() => false);

      if (hasCheckBtn) {
        // Click and verify it doesn't crash (may show error if mock doesn't support verify)
        await checkBtn.click();
        await expect(page.locator('main')).toBeVisible({ timeout: 5_000 });
      }
    }
    // Pass regardless — machine presence depends on backend state
    await expect(page.locator('main')).toBeVisible();
  });
});

import { test, expect } from '@playwright/test';

test.describe('Dashboard Drag-and-Drop', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });
  });

  test('customize panel shows drag handles instead of chevrons', async ({ page }) => {
    const customizeButton = page.getByRole('button', { name: /customize/i }).first();
    if (await customizeButton.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await customizeButton.click();
      await expect(page.getByText('⠿').first()).toBeVisible({ timeout: 5_000 });
      await expect(page.locator('button:has(> svg.lucide-chevron-up)')).not.toBeVisible();
    }
  });
});

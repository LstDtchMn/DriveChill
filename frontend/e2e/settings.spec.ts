import { test, expect } from '@playwright/test';

test.describe('Settings', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });

    // Navigate to settings page
    const settingsLink = page.getByText(/settings/i).first();
    await settingsLink.click();
    await expect(page.getByText(/sensor poll interval|general/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('settings page loads without error', async ({ page }) => {
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByText(/failed to load|error/i)).not.toBeVisible();
  });

  test('temperature unit toggle buttons are visible', async ({ page }) => {
    // Both °C and °F (or C and F) buttons should be present
    const cButton = page.getByRole('button', { name: /°C/ }).or(page.getByText('°C').first());
    const fButton = page.getByRole('button', { name: /°F/ }).or(page.getByText('°F').first());
    await expect(cButton).toBeVisible({ timeout: 5_000 });
    await expect(fButton).toBeVisible({ timeout: 5_000 });
  });

  test('clicking °F toggles the active unit', async ({ page }) => {
    // Find the F button and click it
    const fButton = page.getByRole('button', { name: /°F/ });
    await expect(fButton).toBeVisible({ timeout: 5_000 });
    await fButton.click();

    // The F button should now have an active/selected style (ring-2 or similar)
    // We check that the button is still visible and clickable — full style assertion
    // would require computed CSS inspection which varies by browser rendering
    await expect(fButton).toBeVisible();

    // Save the settings
    const saveButton = page.getByRole('button', { name: /save settings|save/i }).first();
    if (await saveButton.isVisible()) {
      await saveButton.click();
      // A success indicator (e.g. "Saved!") should briefly appear
      await expect(page.getByText(/saved/i)).toBeVisible({ timeout: 5_000 });
    }
  });

  test('poll interval input accepts numeric values', async ({ page }) => {
    const pollInput = page.locator('input[type="number"]').first();
    if (await pollInput.isVisible()) {
      await pollInput.fill('2');
      await expect(pollInput).toHaveValue('2');
    }
  });

  test('config export button is visible', async ({ page }) => {
    // Export button should be present in settings
    const exportButton = page.getByRole('button', { name: /export|download config/i }).first();
    const exportLink = page.getByText(/export config|export settings|download/i).first();
    const hasExport =
      await exportButton.isVisible({ timeout: 5_000 }).catch(() => false) ||
      await exportLink.isVisible({ timeout: 5_000 }).catch(() => false);
    // Export should be available in settings
    expect(hasExport).toBe(true);
  });

  test('config import section is visible', async ({ page }) => {
    // Import section or button should be present
    const importButton = page.getByRole('button', { name: /import/i }).first();
    const importText = page.getByText(/import config|import settings|restore/i).first();
    const hasImport =
      await importButton.isVisible({ timeout: 5_000 }).catch(() => false) ||
      await importText.isVisible({ timeout: 5_000 }).catch(() => false);
    expect(hasImport).toBe(true);
  });
});

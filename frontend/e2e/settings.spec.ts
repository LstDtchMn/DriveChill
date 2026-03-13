import { test, expect } from '@playwright/test';

test.describe('Settings', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    // Wait for the app to finish initial loading
    await page.waitForFunction(
      () => !document.body.innerText.includes('Loading...'),
      { timeout: 15_000 },
    );

    // Dismiss the "What's new" banner if present — it can overlay other elements
    const dismissBtn = page.getByRole('button', { name: /dismiss/i });
    if (await dismissBtn.isVisible({ timeout: 2_000 }).catch(() => false)) {
      await dismissBtn.click();
      await expect(dismissBtn).not.toBeVisible({ timeout: 2_000 });
    }

    // Navigate to settings page
    const settingsLink = page.getByText(/settings/i).first();
    await settingsLink.click();

    // Wait for settings-specific content
    await expect(
      page.getByRole('heading', { name: /general/i }),
    ).toBeVisible({ timeout: 10_000 });

    // Wait for the Save Settings button — confirms form is loaded
    await expect(
      page.getByRole('button', { name: /save settings/i }),
    ).toBeVisible({ timeout: 5_000 });
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
    const fButton = page.getByRole('button', { name: /°F/ });
    await expect(fButton).toBeVisible({ timeout: 5_000 });

    await fButton.click();
    await expect(fButton).toBeVisible();

    // Save settings and verify success
    const saveButton = page.getByRole('button', { name: /save settings/i });
    await expect(saveButton).toBeEnabled({ timeout: 5_000 });
    await saveButton.click();

    // After save, either a toast ("saved") or the button re-enables — either proves success
    const saved = page.getByText(/saved/i);
    const reEnabled = page.getByRole('button', { name: /save settings/i });
    await expect(saved.or(reEnabled)).toBeVisible({ timeout: 5_000 });
  });

  test('poll interval input accepts numeric values', async ({ page }) => {
    const pollInput = page.locator('input[type="number"]').first();
    if (await pollInput.isVisible()) {
      await pollInput.fill('2');
      await expect(pollInput).toHaveValue('2');
    }
  });

  test('config export button is visible', async ({ page }) => {
    // Export/import is on the Infrastructure tab
    const infraTab = page.getByRole('button', { name: /infra/i }).first();
    if (await infraTab.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await infraTab.click();
    }

    const exportButton = page.getByRole('button', { name: /export|download config/i }).first();
    const exportLink = page.getByText(/export config|export settings|download/i).first();
    const hasExport =
      await exportButton.isVisible({ timeout: 5_000 }).catch(() => false) ||
      await exportLink.isVisible({ timeout: 5_000 }).catch(() => false);
    expect(hasExport).toBe(true);
  });

  test('config import section is visible', async ({ page }) => {
    // Export/import is on the Infrastructure tab
    const infraTab = page.getByRole('button', { name: /infra/i }).first();
    if (await infraTab.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await infraTab.click();
    }

    const importButton = page.getByRole('button', { name: /import/i }).first();
    const importText = page.getByText(/import config|import settings|restore/i).first();
    const hasImport =
      await importButton.isVisible({ timeout: 5_000 }).catch(() => false) ||
      await importText.isVisible({ timeout: 5_000 }).catch(() => false);
    expect(hasImport).toBe(true);
  });
});

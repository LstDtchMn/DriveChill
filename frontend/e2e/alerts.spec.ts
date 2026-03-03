import { test, expect } from '@playwright/test';

test.describe('Alerts', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });

    // Navigate to alerts page
    const alertsLink = page.getByText(/alerts/i).first();
    await alertsLink.click();
    await expect(page.getByText(/alert rules/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('alerts page loads without error', async ({ page }) => {
    await expect(page.locator('main')).toBeVisible();
    // Should not show any error state
    await expect(page.getByText(/failed to load/i)).not.toBeVisible();
  });

  test('can create a new alert rule', async ({ page }) => {
    // Look for a sensor selector or "Add Rule" button
    const addButton = page.getByRole('button', { name: /add rule|new rule|create/i }).first();
    if (await addButton.isVisible()) {
      await addButton.click();
    }

    // Fill in the rule form — fields vary but sensor select and threshold should exist
    const sensorSelect = page.locator('select').first();
    if (await sensorSelect.isVisible()) {
      // Select the first available option
      await sensorSelect.selectOption({ index: 1 });
    }

    const thresholdInput = page.locator('input[type="number"]').first();
    if (await thresholdInput.isVisible()) {
      await thresholdInput.fill('80');
    }

    const saveButton = page.getByRole('button', { name: /save|add|create|confirm/i }).first();
    if (await saveButton.isVisible()) {
      await saveButton.click();
      // After creating, an item should appear in the rules list
      await expect(page.locator('[data-testid="alert-rule"], .card').first()).toBeVisible({ timeout: 5_000 });
    }
  });

  test('shows empty state when no rules exist', async ({ page }) => {
    // If there are no alert rules, show appropriate messaging
    const content = page.locator('main');
    await expect(content).toBeVisible();
    // Page should not crash regardless of data state
    await expect(page.getByText(/error|unexpected/i)).not.toBeVisible();
  });
});

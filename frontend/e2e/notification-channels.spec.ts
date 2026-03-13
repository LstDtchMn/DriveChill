import { test, expect } from '@playwright/test';

test.describe('Notification Channels (MQTT structured form)', () => {
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
      await expect(dismissBtn).not.toBeVisible({ timeout: 2_000 });
    }

    // Navigate to settings page
    const settingsLink = page.getByText(/settings/i).first();
    await settingsLink.click();
    await expect(
      page.getByRole('heading', { name: /general/i }),
    ).toBeVisible({ timeout: 10_000 });
  });

  test('notification channels section is visible', async ({ page }) => {
    // Navigate to the infra tab if tabbed layout
    const notificationsTab = page.getByRole('button', { name: /notifications/i }).first();
    if (await notificationsTab.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await notificationsTab.click();
    }

    const heading = page.getByText(/notification channels/i).first();
    await expect(heading).toBeVisible({ timeout: 10_000 });
  });

  test('MQTT type shows structured form fields instead of JSON textarea', async ({ page }) => {
    // Navigate to infra tab
    const notificationsTab = page.getByRole('button', { name: /notifications/i }).first();
    if (await notificationsTab.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await notificationsTab.click();
    }

    // Wait for notification channels section
    await expect(page.getByText(/notification channels/i).first()).toBeVisible({ timeout: 10_000 });

    // Select MQTT type from the dropdown
    const typeSelect = page.locator('select').filter({ hasText: /discord|slack|mqtt/i }).first();
    if (await typeSelect.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await typeSelect.selectOption('mqtt');

      // MQTT structured form fields should appear
      await expect(page.getByText(/broker url/i).first()).toBeVisible({ timeout: 5_000 });
      await expect(page.getByText(/topic prefix/i).first()).toBeVisible({ timeout: 3_000 });
      await expect(page.getByText(/client id/i).first()).toBeVisible({ timeout: 3_000 });

      // QoS dropdown should be visible
      const qosSelect = page.locator('select').filter({ hasText: /at most once|at least once|exactly once/i }).first();
      await expect(qosSelect).toBeVisible({ timeout: 3_000 });

      // Retain and publish telemetry checkboxes
      await expect(page.getByText(/retain messages/i).first()).toBeVisible({ timeout: 3_000 });
      await expect(page.getByText(/publish telemetry/i).first()).toBeVisible({ timeout: 3_000 });

      // JSON textarea should NOT be visible for MQTT
      const jsonTextarea = page.locator('textarea').first();
      await expect(jsonTextarea).not.toBeVisible({ timeout: 2_000 });
    }
  });

  test('switching from MQTT to Discord shows JSON textarea', async ({ page }) => {
    const notificationsTab = page.getByRole('button', { name: /notifications/i }).first();
    if (await notificationsTab.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await notificationsTab.click();
    }

    await expect(page.getByText(/notification channels/i).first()).toBeVisible({ timeout: 10_000 });

    const typeSelect = page.locator('select').filter({ hasText: /discord|slack|mqtt/i }).first();
    if (await typeSelect.isVisible({ timeout: 3_000 }).catch(() => false)) {
      // Select MQTT first
      await typeSelect.selectOption('mqtt');
      await expect(page.getByText(/broker url/i).first()).toBeVisible({ timeout: 5_000 });

      // Switch to Discord — JSON textarea should appear
      await typeSelect.selectOption('discord');
      const textarea = page.locator('textarea').first();
      await expect(textarea).toBeVisible({ timeout: 5_000 });
    }
  });
});

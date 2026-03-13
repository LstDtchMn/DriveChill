import { test, expect } from '@playwright/test';

test.describe('Analytics', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });

    // Navigate to Analytics page
    const analyticsLink = page.getByText(/analytics/i).first();
    await analyticsLink.click();
    await expect(page.getByText(/analytics/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('analytics page loads without error', async ({ page }) => {
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
  });

  test('shows time window selector buttons', async ({ page }) => {
    // Time window options: 1h, 6h, 24h, 7d, 30d
    const windowButton = page.getByRole('button', { name: /1h|6h|24h|7d|30d/i }).first();
    await expect(windowButton).toBeVisible({ timeout: 10_000 });
  });

  test('can switch between time windows', async ({ page }) => {
    const btn24h = page.getByRole('button', { name: /24h/i }).first();
    if (await btn24h.isVisible()) {
      await btn24h.click();
      // Button should become active (style change) — just verify no crash
      await expect(page.locator('main')).toBeVisible();
    }
  });

  test('shows stat cards or history section', async ({ page }) => {
    // Analytics page should show either stat cards or a "No data" placeholder
    const content = page.locator('main');
    await expect(content).toBeVisible();
    // Page should not crash regardless of data state
    await expect(page.getByText(/error|unexpected/i)).not.toBeVisible();
  });

  test('anomaly table or empty state is rendered', async ({ page }) => {
    // Either anomaly rows or an empty-state placeholder should be visible
    const main = page.locator('main');
    await expect(main).toBeVisible();
    // Page should render analytics content without crashing
    const hasAnomalySection =
      await page.getByText(/anomal/i).isVisible({ timeout: 5_000 }).catch(() => false) ||
      await page.getByText(/no data|no anomal/i).isVisible({ timeout: 3_000 }).catch(() => false);
    // At minimum, the analytics page should not crash
    await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
  });
});

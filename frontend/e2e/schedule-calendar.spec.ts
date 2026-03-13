import { test, expect } from '@playwright/test';

test.describe('Schedule Calendar', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });
    const curvesLink = page.getByText(/fan curves/i).first();
    await curvesLink.click();
    await expect(page.locator('main')).toBeVisible({ timeout: 10_000 });
  });

  test('Schedule tab is visible on Fan Curves page', async ({ page }) => {
    const scheduleTab = page.getByRole('button', { name: /schedule/i }).first();
    await expect(scheduleTab).toBeVisible({ timeout: 5_000 });
  });

  test('clicking Schedule tab shows calendar grid', async ({ page }) => {
    const scheduleTab = page.getByRole('button', { name: /schedule/i }).first();
    await scheduleTab.click();
    await expect(page.getByText('Mon')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText('Sun')).toBeVisible({ timeout: 5_000 });
  });

  test('calendar grid shows time labels', async ({ page }) => {
    const scheduleTab = page.getByRole('button', { name: /schedule/i }).first();
    await scheduleTab.click();
    await expect(page.getByText('00:00')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText('12:00')).toBeVisible({ timeout: 5_000 });
  });

  test('page does not crash on schedule tab', async ({ page }) => {
    const scheduleTab = page.getByRole('button', { name: /schedule/i }).first();
    await scheduleTab.click();
    await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
  });

  test('switching between tabs preserves content', async ({ page }) => {
    const scheduleTab = page.getByRole('button', { name: /schedule/i }).first();
    await scheduleTab.click();
    await expect(page.getByText('Mon')).toBeVisible({ timeout: 5_000 });
    const curvesTab = page.getByRole('button', { name: /curves/i }).first();
    await curvesTab.click();
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByText(/uncaught error|runtime error/i)).not.toBeVisible();
  });
});

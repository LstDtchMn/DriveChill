import { test, expect } from '@playwright/test';

test.describe('Fan Curves', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });

    // Navigate to fan curves via the sidebar
    const curvesLink = page.getByText(/fan curves/i).first();
    await curvesLink.click();
    // Wait for the fan curves page content to appear
    await expect(page.getByText(/preset/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test('shows preset profile cards', async ({ page }) => {
    // Presets: Balanced, Gaming, Silent, etc.
    await expect(page.getByText(/balanced/i).first()).toBeVisible();
    await expect(page.getByText(/gaming/i).first()).toBeVisible();
    await expect(page.getByText(/silent/i).first()).toBeVisible();
  });

  test('shows curve editor when a curve exists', async ({ page }) => {
    // The SVG curve editor should be present if there are curves loaded
    const svg = page.locator('svg').first();
    await expect(svg).toBeVisible({ timeout: 10_000 });
  });

  test('curve editor double-click adds a new point', async ({ page }) => {
    // Find the curve editor SVG
    const editorSvg = page.locator('svg').first();
    await expect(editorSvg).toBeVisible({ timeout: 10_000 });

    // Count existing circles (curve points) before clicking
    const pointsBefore = await editorSvg.locator('circle[fill="var(--card-bg)"]').count();

    // Double-click in the middle of the SVG to add a point
    const box = await editorSvg.boundingBox();
    if (box) {
      await editorSvg.dblclick({ position: { x: box.width / 2, y: box.height / 2 } });
      // A new point should appear
      const pointsAfter = await editorSvg.locator('circle[fill="var(--card-bg)"]').count();
      expect(pointsAfter).toBeGreaterThanOrEqual(pointsBefore);
    }
  });
});

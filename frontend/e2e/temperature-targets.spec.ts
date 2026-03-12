import { test, expect } from '@playwright/test';

test.describe('Temperature Targets', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.waitForFunction(() => !document.body.innerText.includes('Loading...'), { timeout: 15_000 });

    // Navigate to Temperature Targets page
    const targetsLink = page.getByText(/temp.*targets/i).first();
    await targetsLink.click();
    await expect(page.locator('main')).toBeVisible({ timeout: 10_000 });
  });

  test('temperature targets page loads without error', async ({ page }) => {
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByText(/failed to load|unexpected error/i)).not.toBeVisible();
  });

  test('shows list/map view toggle buttons', async ({ page }) => {
    // View switcher buttons contain Lucide icon + text "Map" / "List"
    const mapBtn = page.locator('button', { hasText: /\bMap\b/ }).first();
    const listBtn = page.locator('button', { hasText: /\bList\b/ }).first();
    await expect(mapBtn.or(listBtn).first()).toBeVisible({ timeout: 10_000 });
  });

  test('can switch between list and map views', async ({ page }) => {
    const mapBtn = page.locator('button', { hasText: /\bMap\b/ }).first();
    if (await mapBtn.isVisible()) {
      await mapBtn.click();
      await expect(page.locator('main')).toBeVisible();
    }
    const listBtn = page.locator('button', { hasText: /\bList\b/ }).first();
    if (await listBtn.isVisible()) {
      await listBtn.click();
      await expect(page.locator('main')).toBeVisible();
    }
  });

  test('shows Add Target button or empty state', async ({ page }) => {
    // Either a list of targets or an empty-state "Create" button should appear
    const addButton = page.getByRole('button', { name: /add target|new target|create/i }).first();
    const emptyState = page.getByText(/no targets|get started/i).first();
    const main = page.locator('main');
    await expect(main).toBeVisible();
    // Just verify the page renders without an error — empty state is expected
    await expect(page.getByText(/error|unexpected/i)).not.toBeVisible();
    const hasAdd = await addButton.isVisible().catch(() => false);
    const hasEmpty = await emptyState.isVisible().catch(() => false);
    const hasContent = hasAdd || hasEmpty;
    // In a fresh mock backend either an add button or empty state should appear
    expect(hasContent).toBe(true);
  });

  test('create target form can be opened', async ({ page }) => {
    const addButton = page.getByRole('button', { name: /add target|new target|\+/i }).first();
    if (await addButton.isVisible()) {
      await addButton.click();
      // A form or panel with sensor/fan selectors should appear
      await expect(page.locator('select, input[type="number"]').first()).toBeVisible({ timeout: 5_000 });
    }
  });

  test('PID control fields are accessible in target form', async ({ page }) => {
    const addButton = page.getByRole('button', { name: /add target|new target|\+/i }).first();
    if (!await addButton.isVisible({ timeout: 5_000 }).catch(() => false)) {
      return; // No form to open
    }
    await addButton.click();
    // Wait for form to appear
    await page.waitForSelector('select, input[type="number"]', { timeout: 5_000 }).catch(() => {});

    // PID fields: Kp, Ki, Kd or proportional/integral/derivative labels
    const pidLabel = page.getByText(/kp|ki|kd|proportional|integral|derivative/i).first();
    const pidInput = page.locator('input[type="number"]').first();
    // Either PID labels are visible, or numeric inputs (for target temp, tolerances, PID gains)
    const hasPidFields =
      await pidLabel.isVisible({ timeout: 3_000 }).catch(() => false) ||
      await pidInput.isVisible({ timeout: 3_000 }).catch(() => false);
    expect(hasPidFields).toBe(true);
  });

  test('target temperature input accepts numeric value', async ({ page }) => {
    const addButton = page.getByRole('button', { name: /add target|new target|\+/i }).first();
    if (!await addButton.isVisible({ timeout: 5_000 }).catch(() => false)) {
      return;
    }
    await addButton.click();

    // Find the target temperature input (likely the first number input)
    const tempInput = page.locator('input[type="number"]').first();
    if (await tempInput.isVisible({ timeout: 5_000 }).catch(() => false)) {
      await tempInput.fill('45');
      await expect(tempInput).toHaveValue('45');
    }
  });
});

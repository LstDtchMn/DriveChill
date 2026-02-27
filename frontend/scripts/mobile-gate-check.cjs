/* eslint-disable no-console */
const { chromium } = require('playwright');

const BASE_URL = process.env.DRIVECHILL_BASE_URL || 'http://127.0.0.1:8085';
const VIEWPORT = { width: 375, height: 667 };
const MAX_SCROLL_OVERFLOW_PX = 0;
const MIN_TOUCH_TARGET_PX = 44;

async function clickNav(page, label) {
  const button = page.getByRole('button', { name: new RegExp(`^${label}$`, 'i') });
  await button.first().click();
  await page.waitForTimeout(250);
}

async function pageMetrics(page) {
  return page.evaluate((minTouch) => {
    const doc = document.documentElement;
    const body = document.body;
    const scrollWidth = Math.max(
      doc ? doc.scrollWidth : 0,
      body ? body.scrollWidth : 0
    );
    const viewportWidth = window.innerWidth;
    const overflowPx = Math.max(0, scrollWidth - viewportWidth);

    const isVisible = (el) => {
      const style = window.getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') {
        return false;
      }
      const r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    };

    const controls = Array.from(
      document.querySelectorAll(
        'button, a, input:not([type="checkbox"]):not([type="radio"]), select, textarea'
      )
    ).filter(isVisible);

    const tooSmall = controls
      .map((el) => {
        const r = el.getBoundingClientRect();
        return {
          tag: el.tagName.toLowerCase(),
          text: (el.innerText || el.getAttribute('aria-label') || '').trim().slice(0, 40),
          width: Math.round(r.width),
          height: Math.round(r.height),
        };
      })
      .filter((c) => c.width < minTouch || c.height < minTouch);

    return {
      viewportWidth,
      scrollWidth,
      overflowPx,
      controlsChecked: controls.length,
      tooSmall,
    };
  }, MIN_TOUCH_TARGET_PX);
}

async function ensureCurveExists(page) {
  const createButton = page.getByRole('button', { name: /create your first curve/i });
  if (await createButton.count()) {
    await createButton.first().click();
    await page.waitForTimeout(400);
    return;
  }
}

async function checkCurveTouchDrag(page) {
  await page.waitForSelector('svg circle[fill="transparent"]', { timeout: 5000 });
  return page.evaluate(async () => {
    const hit = document.querySelector('svg circle[fill="transparent"]');
    const line = document.querySelector('svg path[stroke="var(--accent)"]');
    if (!hit || !line) {
      return { ok: false, reason: 'curve_editor_not_ready' };
    }

    const before = line.getAttribute('d');
    const rect = hit.getBoundingClientRect();
    const sx = rect.left + rect.width / 2;
    const sy = rect.top + rect.height / 2;
    const pointerId = 77;

    hit.dispatchEvent(
      new PointerEvent('pointerdown', {
        bubbles: true,
        pointerId,
        pointerType: 'touch',
        clientX: sx,
        clientY: sy,
      })
    );

    await new Promise((resolve) => setTimeout(resolve, 40));

    window.dispatchEvent(
      new PointerEvent('pointermove', {
        bubbles: true,
        pointerId,
        pointerType: 'touch',
        clientX: sx + 28,
        clientY: sy - 18,
      })
    );

    await new Promise((resolve) => setTimeout(resolve, 40));

    window.dispatchEvent(
      new PointerEvent('pointerup', {
        bubbles: true,
        pointerId,
        pointerType: 'touch',
        clientX: sx + 28,
        clientY: sy - 18,
      })
    );

    await new Promise((resolve) => setTimeout(resolve, 120));
    const after = line.getAttribute('d');
    return { ok: before !== after, reason: before === after ? 'path_unchanged' : null };
  });
}

async function main() {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({
    viewport: VIEWPORT,
    isMobile: true,
    hasTouch: true,
  });
  const page = await context.newPage();

  const result = {
    baseUrl: BASE_URL,
    viewport: VIEWPORT,
    pages: {},
    curveTouchDrag: null,
    passed: false,
  };

  try {
    await page.goto(BASE_URL, { waitUntil: 'networkidle', timeout: 30000 });

    await clickNav(page, 'Dashboard');
    result.pages.dashboard = await pageMetrics(page);

    await clickNav(page, 'Fan Curves');
    await ensureCurveExists(page);
    result.pages.curves = await pageMetrics(page);
    result.curveTouchDrag = await checkCurveTouchDrag(page);

    await clickNav(page, 'Alerts');
    result.pages.alerts = await pageMetrics(page);

    await clickNav(page, 'Settings');
    result.pages.settings = await pageMetrics(page);

    const overflowFailures = Object.entries(result.pages).filter(
      ([, m]) => m.overflowPx > MAX_SCROLL_OVERFLOW_PX
    );
    const touchFailures = Object.entries(result.pages).filter(
      ([, m]) => m.tooSmall.length > 0
    );
    const curveOk = result.curveTouchDrag && result.curveTouchDrag.ok;

    result.passed = overflowFailures.length === 0 && touchFailures.length === 0 && curveOk;

    console.log(JSON.stringify(result, null, 2));
    process.exit(result.passed ? 0 : 1);
  } finally {
    await context.close();
    await browser.close();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});

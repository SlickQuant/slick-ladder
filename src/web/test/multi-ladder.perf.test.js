// Multi-ladder performance regression (node:test). Many ladders, each with its own random-rate
// feed, on a simulated 60 FPS clock: every frame ingests the updates due since the last frame,
// then runs every ladder's rAF frame (idle ladders skip theirs). Scenarios: price levels, MBO
// orders, and a deep book (500 levels a side). Fails if the average frame exceeds the budget, p95
// exceeds 1.5x the budget, or a ladder's book drifts from its feed. Canvas calls are no-ops: this
// measures library work, not browser rasterization. Mirrors
// tests/SlickUI.Core.Tests/MultiLadderPerformanceTests.cs.
// SLICK_SKIP_PERF=1 skips it (CI), SLICK_PERF_FRAME_BUDGET_MS overrides the budget
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PriceLadder, fakeContainer, frame: runFrame } = require('../test-utils/ladder-harness');
const { Feed, OrderFeed, TICK_SIZE } = require('../test-utils/random-feed');

const FRAME_MS = 1000 / 60;
const WARMUP_FRAMES = 60;
const MEASURED_FRAMES = 300;
const FRAME_BUDGET_MS = Number(process.env.SLICK_PERF_FRAME_BUDGET_MS) || FRAME_MS;
const P95_BUDGET_MS = FRAME_BUDGET_MS * 1.5; // Room for scheduler noise: the average holds the 60 FPS line

function percentile(sorted, p) {
    return sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)];
}

function summarize(values) {
    const sorted = [...values].sort((a, b) => a - b);
    const avg = values.reduce((sum, v) => sum + v, 0) / values.length;
    return { avg, p95: percentile(sorted, 0.95), max: sorted[sorted.length - 1] };
}

// Name, feed (data mode, levels per side) and ladder counts
const SCENARIOS = [
    ['price levels', { mode: 'PriceLevel', depth: 40 }, [50, 100]],
    ['MBO orders', { mode: 'MBO', depth: 40 }, [50, 100]],
    ['500-level books', { mode: 'PriceLevel', depth: 500 }, [100]]
];
const RUNS = SCENARIOS.flatMap(([name, settings, counts]) => counts.map((ladderCount) => ({ name, settings, ladderCount })));

// Half the ladders in each removal mode so both render paths are covered
function runScenario(ladderCount, { mode, depth }) {
    const mbo = mode === 'MBO';
    const feeds = Array.from({ length: ladderCount }, (_, i) => mbo ? new OrderFeed(1000 + i, depth) : new Feed(1000 + i, depth));
    const ladders = feeds.map((feed, i) => {
        const ladder = new PriceLadder({
            container: fakeContainer(400),
            height: 600,
            tickSize: TICK_SIZE,
            mode,
            removalMode: i % 2 === 0 ? 'removeRow' : 'showEmpty'
        });
        feedLadder(ladder, mbo, feed.seed());
        return ladder;
    });

    const ingestMs = [];
    const renderMs = [];
    const totalMs = [];
    let updates = 0;
    for (let frame = 0; frame < WARMUP_FRAMES + MEASURED_FRAMES; frame++) {
        // Generating updates is the network's job, so it stays out of the timing
        const due = feeds.map((feed) => feed.advance((frame + 1) * FRAME_MS));

        const start = performance.now();
        for (let i = 0; i < ladderCount; i++) {
            for (const batch of due[i]) feedLadder(ladders[i], mbo, batch);
        }
        const ingested = performance.now();
        for (const ladder of ladders) runFrame(ladder);
        const end = performance.now();

        if (frame >= WARMUP_FRAMES) {
            ingestMs.push(ingested - start);
            renderMs.push(end - ingested);
            totalMs.push(end - start);
            for (const ticks of due) for (const batch of ticks) updates += batch.length;
        }
    }
    return { feeds, ladders, updates, ingest: summarize(ingestMs), render: summarize(renderMs), total: summarize(totalMs) };
}

const feedLadder = (ladder, mbo, batch) => mbo ? ladder.processOrderBatch(batch) : ladder.processBatch(batch);
const toTick = (price) => (price === null ? null : Math.round(price / TICK_SIZE));
const skip = process.env.SLICK_SKIP_PERF ? 'SLICK_SKIP_PERF is set' : false;

for (const { name, settings, ladderCount } of RUNS) {
    test(`${ladderCount} ladders, ${name}: random-rate feeds keep up at 60 FPS`, { skip }, (t) => {
        const { feeds, ladders, updates, ingest, render: draw, total } = runScenario(ladderCount, settings);
        const seconds = MEASURED_FRAMES / 60;
        const ms = (v) => `${v.toFixed(2)}ms`;
        t.diagnostic(`${ladderCount} ladders, ${updates} updates in ${seconds}s (${Math.round(updates / seconds)}/s)`);
        t.diagnostic(`frame avg ${ms(total.avg)} p95 ${ms(total.p95)} max ${ms(total.max)}, budget ${ms(FRAME_BUDGET_MS)} (p95 ${ms(P95_BUDGET_MS)})`);
        t.diagnostic(`ingest avg ${ms(ingest.avg)} p95 ${ms(ingest.p95)}, render avg ${ms(draw.avg)} p95 ${ms(draw.p95)}`);

        // A timing means nothing if updates were lost or crossed between ladders
        ladders.forEach((ladder, i) => {
            const feed = feeds[i];
            const metrics = ladder.getMetrics();
            assert.equal(metrics.updateCount, feed.updateCount, `ladder ${i} update count`);
            assert.equal(metrics.bidLevels, feed.bids.size, `ladder ${i} bid levels`);
            assert.equal(metrics.askLevels, feed.asks.size, `ladder ${i} ask levels`);
            assert.equal(toTick(ladder.getBestBid()), feed.bestBid(), `ladder ${i} best bid`);
            assert.equal(toTick(ladder.getBestAsk()), feed.bestAsk(), `ladder ${i} best ask`);
            // The last frame rendered the current book, not a stale snapshot
            assert.equal(toTick(ladder.renderer.currentSnapshot.bestBid), feed.bestBid(), `ladder ${i} rendered best bid`);
            ladder.destroy();
        });

        assert.ok(total.avg <= FRAME_BUDGET_MS,
            `average frame ${ms(total.avg)} over the ${ms(FRAME_BUDGET_MS)} budget with ${ladderCount} ladders`);
        assert.ok(total.p95 <= P95_BUDGET_MS,
            `p95 frame ${ms(total.p95)} over ${ms(P95_BUDGET_MS)} with ${ladderCount} ladders`);
    });
}

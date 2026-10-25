import assert from 'node:assert/strict'
import { SnapshotOrder } from '../../src/GrimDawnOptimizer.Desktop/src/lib/state-order.ts'

const order = new SnapshotOrder()
assert.equal(order.accept({ sessionId: 'engine-a', revision: 1 }), true)
assert.equal(order.accept({ sessionId: 'engine-a', revision: 3 }), true)
assert.equal(order.accept({ sessionId: 'engine-a', revision: 2 }), false, 'late command response must not restore stale results')
assert.equal(order.accept({ sessionId: 'engine-a', revision: 3 }), false, 'duplicate snapshots do not trigger redundant updates')
assert.equal(order.accept({ sessionId: 'engine-b', revision: 1 }), true, 'restarted engine can begin at revision one')
assert.equal(order.accept({ sessionId: 'engine-a', revision: 4 }), false, 'late old-process response cannot revive a retired session')
assert.equal(order.accept({ sessionId: 'engine-b', revision: 2 }), true)
assert.equal(order.accept({ sessionId: '', revision: 1 }), false)
assert.equal(order.accept({ sessionId: 'engine-b', revision: Number.NaN }), false)
console.log('PASS: response/event ordering, duplicates, restart, retired session, malformed cursor')

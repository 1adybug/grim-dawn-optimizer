import assert from 'node:assert/strict'
import { createServer, get } from 'node:http'
import { SseChannel } from '../../src/GrimDawnOptimizer.Desktop/scripts/dev-engine.ts'

// A result-shaped snapshot substantially exceeds Node's writable high-water
// mark. The test uses the actual channel and real HTTP sockets, not a mocked
// write return value, and confirms later events survive the large first frame.
const longSource = '共享仓库第 8 页 · 首选附件与备选获取位置；'.repeat(22)
const results = Array.from({ length: 10 }, (_, index) => ({
  rank: index + 1,
  title: `候选 ${index + 1}`,
  slotRows: Array.from({ length: 14 }, (_, slot) => ({
    slot: `槽位 ${slot}`,
    currentItem: '当前装备', candidateItem: '神话装备',
    component: longSource, augment: longSource, source: longSource, changed: true,
  })),
  singleSwaps: Array.from({ length: 14 }, (_, swap) => ({
    index: swap, title: '单件换装对照', explanation: longSource,
    rows: Array.from({ length: 20 }, (_, metric) => ({
      key: `metric-${metric}`, label: '主攻面板与抗性', before: '23,478',
      predicted: '25,000', delta: 1522, change: '+1522', actualInput: '', deviation: '—',
    })),
  })),
}))
const snapshot = revision => ({ protocolVersion: 1, sessionId: 'large-results', revision, ready: true, isBusy: false, results })
const payloadBytes = Buffer.byteLength(JSON.stringify(snapshot(1)))
assert.ok(payloadBytes > 64 * 1024)

let channel
let channelClosed = false
let prematureEnd = false
let response
let request
const events = []
const waiters = []
const server = createServer((_, outgoing) => {
  outgoing.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' })
  channel = new SseChannel(outgoing, () => { channelClosed = true })
  channel.send('state', snapshot(1))
  // All sends happen before drain. Only the last unsent state should remain,
  // while the error event must survive coalescing.
  for (let revision = 2; revision <= 30; revision++) {
    channel.send('state', snapshot(revision))
    channel.send('trainer', { revision, connected: false })
  }
  channel.send('engine-error', { error: '可恢复的测试诊断' })
  channel.send('state', snapshot(31))
  channel.send('trainer', { revision: 31, connected: false })
})

function awaitEvent(predicate) {
  const existing = events.find(predicate)
  if (existing) return Promise.resolve(existing)
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Timed out waiting for an SSE frame')), 10_000)
    waiters.push(event => { if (predicate(event)) { clearTimeout(timer); resolve(event) } })
  })
}

try {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  await new Promise((resolve, reject) => {
    request = get(`http://127.0.0.1:${server.address().port}`, incoming => {
      response = incoming
      incoming.setEncoding('utf8')
      let buffer = ''
      incoming.on('data', chunk => {
        buffer += chunk
        let end
        while ((end = buffer.indexOf('\n\n')) >= 0) {
          const frame = buffer.slice(0, end)
          buffer = buffer.slice(end + 2)
          const name = /^event: (.+)$/m.exec(frame)?.[1]
          const data = /^data: (.+)$/m.exec(frame)?.[1]
          if (!name || !data) continue
          const event = { name, data: JSON.parse(data) }
          events.push(event)
          for (const waiter of waiters) waiter(event)
        }
      })
      incoming.on('end', () => { prematureEnd = true })
      incoming.on('error', reject)
      resolve()
    })
    request.on('error', reject)
  })
  await awaitEvent(event => event.name === 'state' && event.data.revision === 31)
  await awaitEvent(event => event.name === 'engine-error')
  await awaitEvent(event => event.name === 'trainer' && event.data.revision === 31)
  assert.equal(channelClosed, false, 'normal write backpressure must not close a healthy stream')
  assert.equal(prematureEnd, false)
  assert.deepEqual(events.filter(event => event.name === 'state').map(event => event.data.revision), [1, 31], 'superseded unsent snapshots are coalesced')
  assert.deepEqual(events.filter(event => event.name === 'trainer').map(event => event.data.revision), [31], 'trainer snapshots coalesce independently without replacing optimizer state')
  channel.send('state', snapshot(32))
  const later = await awaitEvent(event => event.name === 'state' && event.data.revision === 32)
  assert.equal(later.data.results[9].slotRows.length, 14)
  assert.equal(later.data.results[0].slotRows[0].component, longSource, 'UTF-8 survives split HTTP chunks')
  assert.equal(channelClosed, false)
  assert.equal(prematureEnd, false)
  channel.send('trainer', { revision: 32, connected: false })
  await awaitEvent(event => event.name === 'trainer' && event.data.revision === 32)
  console.log(`PASS: real HTTP SSE ${payloadBytes.toLocaleString()}-byte result snapshots; independent trainer/state coalescing; error retention; later updates; UTF-8`)
} finally {
  response?.destroy()
  request?.destroy()
  channel?.close()
  await new Promise(resolve => server.close(resolve))
}

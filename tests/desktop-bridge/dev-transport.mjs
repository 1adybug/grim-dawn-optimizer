// Node 22.18+ / 24: the built-in TypeScript loader reads the Vite plugin directly.
// Uses a real, already-built .NET bridge and an isolated temporary data directory.
import assert from 'node:assert/strict'
import { mkdtemp, rm } from 'node:fs/promises'
import { request as httpRequest } from 'node:http'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = fileURLToPath(new URL('../../', import.meta.url))
const desktop = resolve(root, 'src/GrimDawnOptimizer.Desktop')
const { createServer } = await import(pathToFileURL(resolve(desktop, 'node_modules/vite/dist/node/index.js')))
const { devEngine } = await import(pathToFileURL(resolve(desktop, 'scripts/dev-engine.ts')))
const directory = await mkdtemp(resolve(tmpdir(), 'grim-dawn-dev-transport-'))
const previousDirectory = process.env.GRIM_DAWN_DATA_DIR
process.env.GRIM_DAWN_DATA_DIR = directory
const server = await createServer({
  configFile: false, root: desktop, plugins: [devEngine()],
  server: { host: '127.0.0.1', port: 1421, strictPort: true },
  optimizeDeps: { noDiscovery: true, include: [] },
  logLevel: 'error',
})
// An isolated listener avoids competing with the developer's real port 1420.
// Its HTTP Host stays the exact application host enforced by the middleware.
const request = (path, method, body, headers = {}, streaming = false) => new Promise((resolve, reject) => {
  const message = body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body)
  const client = httpRequest({ hostname: '127.0.0.1', port: 1421, path, method,
    headers: { Host: '127.0.0.1:1420', 'Content-Type': 'application/json', ...headers } }, response => {
    if (streaming) { resolve(response); return }
    const chunks = []
    response.on('data', chunk => chunks.push(chunk))
    response.on('end', () => {
      const text = Buffer.concat(chunks).toString('utf8')
      resolve({ status: response.statusCode, json: async () => JSON.parse(text) })
    })
  })
  client.on('error', reject)
  client.end(message)
})
const post = (body, headers = {}) => request('/__engine/request', 'POST', body, headers)
let stream
let replayStream

try {
  await server.listen()
  assert.equal((await post({ command: 'state' }, { Origin: 'https://example.com' })).status, 403)
  assert.equal((await post({ command: 'state' }, { Host: 'localhost:1420' })).status, 403)
  assert.equal((await post({ command: 'state' }, { 'Content-Type': 'text/plain' })).status, 415)
  assert.equal((await post('{')).status, 400)
  assert.equal((await post({})).status, 400)
  assert.equal((await post({ command: 'state', payload: 'x'.repeat(70_000) })).status, 413)
  assert.equal((await request('/__engine/request', 'GET')).status, 405)

  stream = await request('/__engine/events', 'GET', undefined, {}, true)
  assert.match(stream.headers['content-type'], /text\/event-stream/)
  let eventText = ''
  const hasState = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('SSE state timeout')), 15_000)
    stream.on('data', chunk => {
      eventText += chunk.toString('utf8')
      if (eventText.includes('event: state\n')) { clearTimeout(timer); resolve() }
    })
    stream.on('error', reject)
  })
  const initial = await post({ command: 'state' })
  assert.equal(initial.status, 200)
  let state = await initial.json()
  for (let count = 0; !state.ready && count < 100; count++) {
    await new Promise(resolve => setTimeout(resolve, 100))
    state = await (await post({ command: 'state' })).json()
  }
  assert.equal(state.ready, true)
  assert.equal(state.protocolVersion, 1)
  const demo = await (await post({ command: 'demo' })).json()
  assert.equal(demo.characters.length, 1)
  assert.equal(demo.equipment.length, 14)
  assert.equal(demo.canOptimize, true)
  await hasState
  assert.match(eventText, /event: state\ndata: \{"protocolVersion":1/)
  const trainer = await (await post({ command: 'trainerState' })).json()
  assert.equal(trainer.connected, false)
  assert.equal(trainer.features.length, 9)
  assert.equal(trainer.results, undefined, 'trainer replies do not contain optimizer results')
  const changedTrainer = await (await post({ command: 'trainerSetMovementMultiplier', payload: { value: 4 } })).json()
  assert.equal(changedTrainer.movementMultiplier, 4)
  const invalidTrainer = await post({ command: 'trainerSetMovementMultiplier', payload: { value: 3 } })
  assert.equal(invalidTrainer.status, 503)
  assert.equal(typeof (await invalidTrainer.json()).error, 'string')
  // An invalid picker kind is rejected before creating any native dialog. Its
  // {path:null} response must never become an optimizer/trainer state event.
  const beforePickerEvents = eventText.length
  const invalidPicker = await post({ command: 'pickPath', payload: { kind: 'invalid-test-kind' } })
  assert.equal(invalidPicker.status, 503)
  assert.equal(typeof (await invalidPicker.json()).error, 'string')
  assert.doesNotMatch(eventText.slice(beforePickerEvents), /data: \{"path":null\}/)

  // A newly connected SSE client must receive both independent cached states,
  // including the trainer rejection, without trainer responses overwriting demo.
  replayStream = await request('/__engine/events', 'GET', undefined, {}, true)
  const replayed = await new Promise((resolve, reject) => {
    let buffer = ''
    const frames = new Map()
    const timer = setTimeout(() => reject(new Error('SSE independent cache replay timeout')), 5_000)
    replayStream.on('data', chunk => {
      buffer += chunk.toString('utf8')
      let end
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const frame = buffer.slice(0, end)
        buffer = buffer.slice(end + 2)
        const name = /^event: (.+)$/m.exec(frame)?.[1]
        const data = /^data: (.+)$/m.exec(frame)?.[1]
        if (name && data) frames.set(name, JSON.parse(data))
      }
      if (frames.has('state') && frames.has('trainer')) { clearTimeout(timer); resolve(frames) }
    })
    replayStream.on('error', reject)
  })
  assert.equal(replayed.get('state').characters.length, 1)
  assert.equal(replayed.get('state').features, undefined)
  assert.equal(replayed.get('trainer').features.length, 9)
  assert.equal(replayed.get('trainer').movementMultiplier, 4)
  assert.equal(typeof replayed.get('trainer').error, 'string')
  assert.equal(replayed.get('trainer').results, undefined)
  replayStream.destroy()
  const invalid = await post({ command: 'unsupportedCommand' })
  assert.equal(invalid.status, 503)
  assert.equal(typeof (await invalid.json()).error, 'string')
  stream.destroy()
  console.log('PASS: real HTTP/SSE bridge; loopback/origin/host guards; JSON and body limits; independent optimizer/trainer states, errors and cache replay; picker errors cannot replace either cache; demo')
} finally {
  stream?.destroy()
  replayStream?.destroy()
  await server.close()
  // The plugin permits 5s engine cancellation plus a bounded flush window.
  await new Promise(resolve => setTimeout(resolve, 500))
  if (previousDirectory === undefined) delete process.env.GRIM_DAWN_DATA_DIR
  else process.env.GRIM_DAWN_DATA_DIR = previousDirectory
  await rm(directory, { recursive: true, force: true, maxRetries: 4, retryDelay: 500 })
}

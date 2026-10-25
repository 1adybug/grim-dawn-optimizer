import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { equipmentColumns, equipmentColumnPinning, equipmentLayoutStorageKey, equipmentSizingPreferencesAfterChange, equipmentVisibleColumnSizing, estimateEquipmentTextWidth, keyboardColumnSize, parseEquipmentColumnSizing, predictEquipmentColumnWidths, readEquipmentColumnSizing, sanitizeEquipmentColumnSizing, splitAttachment, toEquipmentComparisonRows, writeEquipmentColumnSizing } from '../../src/GrimDawnOptimizer.Desktop/src/components/equipment-table-model.ts'

const require = createRequire(new URL('../../src/GrimDawnOptimizer.Desktop/package.json', import.meta.url))
const { createTable, getCoreRowModel } = require('@tanstack/react-table')
const original = [{ slot: '戒指 II', currentItem: '原装戒指', candidateItem: '候选戒指',
  component: '坚硬甲壳\r\n首选：个人仓库第 2 页\r\n备选：铁匠制作（配方已学会）',
  augment: '阵营增幅\n首选：阵营军需官（崇敬）', source: '共享仓库第 4 页', changed: true }]
const data = toEquipmentComparisonRows(original)
assert.equal(data[0].componentName, '坚硬甲壳')
assert.equal(data[0].componentSource, '首选：个人仓库第 2 页\n备选：铁匠制作（配方已学会）')
assert.equal(data[0].augmentSource, '首选：阵营军需官（崇敬）')
assert.equal(data[0].component, original[0].component, 'the complete source value remains available')
assert.deepEqual(splitAttachment('—'), { name: '—', source: '—' })

assert.ok(estimateEquipmentTextWidth('装备') > estimateEquipmentTextWidth('ii'), 'CJK glyphs need more space than narrow Latin glyphs')
assert.ok(estimateEquipmentTextWidth('WWWW') > estimateEquipmentTextWidth('iiii'), 'Latin text uses proportional estimates')
assert.equal(estimateEquipmentTextWidth('e\u0301'), estimateEquipmentTextWidth('e'), 'combining marks do not consume another character width')
assert.equal(estimateEquipmentTextWidth('首选\n备选'), estimateEquipmentTextWidth('首选 · 备选'), 'measure the same single-line separators rendered by cells')
const sizingRows = [...original, { ...original[0], candidateItem: 'Mythical 破晓者的神圣战甲与永恒守望徽记' }]
let defaults = predictEquipmentColumnWidths(sizingRows)
assert.ok(defaults.candidateItem > predictEquipmentColumnWidths(original).candidateItem, 'default widths consider candidates beyond the current selection')
assert.ok(defaults.slot >= estimateEquipmentTextWidth('戒指 II') + 24 + 17, 'slot defaults include padding and the change marker')
const longSourceDefaults = predictEquipmentColumnWidths([{ ...original[0], component: `组件名称\n${'首选与备选位置'.repeat(200)}` }])
assert.equal(longSourceDefaults.componentSource, 400, 'long sources have a bounded reasonable default')
assert.equal(predictEquipmentColumnWidths([]).componentSource, 128, 'empty data still has a usable header/minimum width')

const saved = new Map()
const storage = {
  getItem: key => saved.get(key) ?? null,
  setItem: (key, value) => saved.set(key, value),
  removeItem: key => saved.delete(key),
}
assert.deepEqual(parseEquipmentColumnSizing('{'), {})
assert.deepEqual(parseEquipmentColumnSizing('null'), {})
assert.deepEqual(parseEquipmentColumnSizing(JSON.stringify({ schemaVersion: 2, columnSizing: { currentItem: 300 } })), {})
assert.deepEqual(parseEquipmentColumnSizing(JSON.stringify({ schemaVersion: 1, columnSizing: [] })), {})
assert.deepEqual(parseEquipmentColumnSizing('x'.repeat(9000)), {})
assert.deepEqual(parseEquipmentColumnSizing('{"schemaVersion":1,"columnSizing":{"slot":1e309,"candidateItem":-5,"currentItem":"280","source":280,"unknown":250,"__proto__":300}}'), { source: 280 })
assert.deepEqual(sanitizeEquipmentColumnSizing({ slot: 100000, candidateItem: Number.NaN, source: 5000, currentItem: 0 }), { slot: 96, source: 4000 })
const unavailableStorage = { getItem() { throw new Error('blocked') }, setItem() { throw new Error('full') }, removeItem() { throw new Error('blocked') } }
assert.deepEqual(readEquipmentColumnSizing(unavailableStorage), {})
assert.equal(writeEquipmentColumnSizing({ currentItem: 300 }, unavailableStorage), false)
assert.equal(writeEquipmentColumnSizing({}, unavailableStorage), false)

let width = 900
let state
const table = createTable({
  data,
  columns: equipmentColumns(width, {}, defaults),
  getCoreRowModel: getCoreRowModel(),
  getRowId: row => row.slot,
  columnResizeMode: 'onChange',
  columnResizeDirection: 'ltr',
  enableColumnResizing: true,
  autoResetAll: false,
  initialState: { columnPinning: equipmentColumnPinning },
  state: {},
  onStateChange: updater => {
    state = typeof updater === 'function' ? updater(state) : updater
    state.columnSizing = sanitizeEquipmentColumnSizing(state.columnSizing)
    writeEquipmentColumnSizing(state.columnSizing, storage)
    synchronize()
  },
  renderFallbackValue: null,
})
state = { ...table.initialState }
function synchronize() {
  table.setOptions(previous => ({ ...previous, state, columns: equipmentColumns(width, state.columnSizing, defaults) }))
}
synchronize()
assert.equal(table.getTotalSize(), Object.values(defaults).reduce((total, value) => total + value, 0))
assert.equal(table.getColumn('slot').getIsPinned(), 'left')
assert.equal(table.getColumn('candidateItem').getIsPinned(), 'left')
assert.equal(table.getColumn('candidateItem').getStart('left'), defaults.slot)
assert.equal(table.getColumn('currentItem').getIsPinned(), false)

// Exercise TanStack's actual mouse drag handler with a standalone EventTarget.
// No app/window is started. onChange must update size and sticky offset before
// mouseup, and the document listeners must be released at the end of the drag.
const pointerDocument = new EventTarget()
const pointer = (type, clientX) => {
  const event = new Event(type)
  Object.defineProperty(event, 'clientX', { value: clientX })
  return event
}
const slotHeader = table.getHeaderGroups()[0].headers.find(header => header.column.id === 'slot')
slotHeader.getResizeHandler(pointerDocument)(pointer('mousedown', 100))
assert.equal(table.getState().columnSizingInfo.isResizingColumn, 'slot')
pointerDocument.dispatchEvent(pointer('mousemove', 112))
const resizedSlot = Math.min(defaults.slot + 12, 96)
assert.equal(table.getColumn('slot').getSize(), resizedSlot, 'columnResizeMode onChange updates while the drag is active')
assert.equal(table.getColumn('candidateItem').getStart('left'), resizedSlot, 'sticky offset follows preceding resized column')
pointerDocument.dispatchEvent(pointer('mouseup', 112))
assert.equal(table.getState().columnSizingInfo.isResizingColumn, false)
pointerDocument.dispatchEvent(pointer('mousemove', 145))
assert.equal(table.getColumn('slot').getSize(), resizedSlot, 'pointer handlers are removed on mouseup')

assert.equal(keyboardColumnSize('ArrowRight', 184, 112, 420, 184), 192)
assert.equal(keyboardColumnSize('ArrowLeft', 120, 112, 420, 184, true), 112)
assert.equal(keyboardColumnSize('Home', 184, 112, 420, 184), 112)
assert.equal(keyboardColumnSize('End', 184, 112, 420, 184), 420)
assert.equal(keyboardColumnSize('Enter', 320, 112, 420, 184), 184)
assert.equal(keyboardColumnSize('Tab', 184, 112, 420, 184), undefined)
table.setColumnSizing(previous => ({ ...previous, currentItem: 312, candidateItem: 250 }))
assert.equal(table.getColumn('currentItem').getSize(), 312)
assert.deepEqual(readEquipmentColumnSizing(storage), { slot: resizedSlot, candidateItem: 250, currentItem: 312 }, 'final drag/key preferences are persisted for unmount/reload')
const reload = createTable({
  data,
  columns: equipmentColumns(width, readEquipmentColumnSizing(storage), defaults),
  getCoreRowModel: getCoreRowModel(),
  state: { ...table.initialState, columnSizing: readEquipmentColumnSizing(storage), columnPinning: equipmentColumnPinning },
  onStateChange: () => {}, renderFallbackValue: null,
})
assert.equal(reload.getColumn('currentItem').getSize(), 312, 'a newly mounted table restores the persisted width')
assert.equal(reload.getColumn('candidateItem').getStart('left'), resizedSlot)

// A filtered/candidate-swapped row set never owns or resets the layout state.
table.setOptions(previous => ({ ...previous, data: [] }))
assert.equal(table.getRowModel().rows.length, 0)
assert.equal(table.getColumn('currentItem').getSize(), 312)
assert.equal(table.getColumn('componentSource').getSize(), defaults.componentSource, 'filtering and switching candidates leave the complete-data defaults stable')

defaults = predictEquipmentColumnWidths([...sizingRows, { ...original[0], currentItem: '当前装备名称'.repeat(20) }])
synchronize()
assert.equal(table.getColumn('currentItem').getSize(), 312, 'explicit user width takes priority over new content predictions')
table.getColumn('currentItem').resetSize()
assert.equal(table.getColumn('currentItem').getSize(), defaults.currentItem, 'single-column reset uses the current complete-data prediction')
assert.equal(readEquipmentColumnSizing(storage).currentItem, undefined, 'single-column reset deletes its persisted override')
assert.equal(readEquipmentColumnSizing(storage).candidateItem, 250, 'single-column reset preserves other overrides')
table.setOptions(previous => ({ ...previous, data: toEquipmentComparisonRows([{ ...original[0], candidateItem: '另一候选' }]) }))
assert.equal(table.getRowModel().rows[0].getValue('candidateItem'), '另一候选')
assert.equal(table.getColumn('currentItem').getSize(), defaults.currentItem, 'candidate changes preserve the reset complete-data default')

width = 320
synchronize()
assert.equal(table.getColumn('slot').getSize(), 80)
assert.equal(table.getColumn('candidateItem').getStart('left'), 80)
assert.equal(table.getLeftTotalSize(), 192, 'fixed columns leave visible room for horizontally scrollable columns')
assert.equal(table.getColumn('currentItem').getSize(), defaults.currentItem, 'ordinary columns are not compressed to fit the viewport')
assert.equal(readEquipmentColumnSizing(storage).slot, resizedSlot, 'temporary narrow-window clamping does not overwrite the saved preference')
assert.ok(table.getTotalSize() > width, 'the table retains horizontal overflow')
width = 900
synchronize()
assert.equal(table.getColumn('slot').getSize(), resizedSlot)
assert.equal(table.getColumn('candidateItem').getSize(), 250, 'widening restores the user-selected fixed widths')
table.getColumn('slot').resetSize()
assert.equal(table.getColumn('candidateItem').getStart('left'), defaults.slot)
table.resetColumnSizing()
assert.equal(table.getTotalSize(), Object.values(defaults).reduce((total, value) => total + value, 0))
assert.deepEqual(table.getState().columnSizing, {})
assert.equal(saved.has(equipmentLayoutStorageKey), false, 'reset layout removes the saved overrides completely')

// The UI keeps presentation widths separate from authored preferences. Exercise
// that controlled-state integration with actual TanStack drag/reset updaters.
const fillDefaults = predictEquipmentColumnWidths(original)
let fillPreferences = { currentItem: 200, source: 180 }
let fillViewport = 800
let fillVisible = equipmentVisibleColumnSizing(fillViewport, fillPreferences, fillDefaults)
fillViewport = Object.values(fillVisible).reduce((sum, size) => sum + size, 0) + 100
let fillState
const filled = createTable({
  data,
  columns: equipmentColumns(fillViewport, fillPreferences, fillDefaults),
  getCoreRowModel: getCoreRowModel(),
  columnResizeMode: 'onChange',
  initialState: { columnPinning: equipmentColumnPinning },
  state: {},
  onStateChange: updater => {
    fillState = typeof updater === 'function' ? updater(fillState) : updater
    synchronizeFill()
  },
  onColumnSizingChange: updater => {
    const nextVisible = typeof updater === 'function' ? updater(fillVisible) : updater
    fillPreferences = equipmentSizingPreferencesAfterChange(fillPreferences, fillVisible, nextVisible)
    writeEquipmentColumnSizing(fillPreferences, storage)
    synchronizeFill()
  },
  renderFallbackValue: null,
})
fillState = { ...filled.initialState }
function synchronizeFill() {
  fillVisible = equipmentVisibleColumnSizing(fillViewport, fillPreferences, fillDefaults)
  fillState = { ...fillState, columnSizing: fillVisible }
  filled.setOptions(previous => ({ ...previous, state: fillState, columns: equipmentColumns(fillViewport, fillPreferences, fillDefaults) }))
}
synchronizeFill()
assert.equal(filled.getTotalSize(), fillViewport, 'the final column fills unused viewport space exactly')
assert.equal(filled.getColumn('source').getSize(), 280)
assert.equal(filled.getColumn('candidateItem').getStart('left'), fillDefaults.slot)
assert.deepEqual(fillPreferences, { currentItem: 200, source: 180 }, 'initial filling does not replace authored widths')
filled.setColumnSizing(previous => ({ ...previous, currentItem: 208 }))
assert.equal(filled.getTotalSize(), fillViewport)
assert.equal(filled.getColumn('source').getSize(), 272)
assert.deepEqual(readEquipmentColumnSizing(storage), { currentItem: 208, source: 180 }, 'resizing another column does not persist the expanded last column')
const beforeSourceDrag = filled.getColumn('source').getSize()
const sourceHeader = filled.getHeaderGroups()[0].headers.find(header => header.column.id === 'source')
sourceHeader.getResizeHandler(pointerDocument)(pointer('mousedown', 100))
pointerDocument.dispatchEvent(pointer('mousemove', 124))
assert.equal(filled.getColumn('source').getSize(), beforeSourceDrag + 24, 'last-column drag starts from its visible width, with no jump to its stored width')
pointerDocument.dispatchEvent(pointer('mouseup', 124))
assert.equal(readEquipmentColumnSizing(storage).source, beforeSourceDrag + 24, 'an explicit last-column edit becomes its authored target width')
assert.equal(filled.getTotalSize(), fillViewport + 24, 'growing the last column beyond the filled viewport produces horizontal overflow')
filled.getColumn('source').resetSize()
assert.equal(readEquipmentColumnSizing(storage).source, undefined)
assert.equal(readEquipmentColumnSizing(storage).currentItem, 208)
assert.equal(filled.getTotalSize(), fillViewport, 'reset restores the predicted width plus current viewport fill')

fillViewport = 2800
synchronizeFill()
const wideSource = filled.getColumn('source').getSize()
assert.ok(wideSource > 560, 'wide-window regression exceeds the former last-column resize limit')
assert.equal(filled.getTotalSize(), fillViewport)
assert.equal(readEquipmentColumnSizing(storage).source, undefined, 'window expansion does not persist presentation fill')
const wideSourceHeader = filled.getHeaderGroups()[0].headers.find(header => header.column.id === 'source')
wideSourceHeader.getResizeHandler(pointerDocument)(pointer('mousedown', 100))
pointerDocument.dispatchEvent(pointer('mouseup', 100))
assert.equal(readEquipmentColumnSizing(storage).source, undefined, 'a zero-movement drag does not author a filled width')
wideSourceHeader.getResizeHandler(pointerDocument)(pointer('mousedown', 100))
pointerDocument.dispatchEvent(pointer('mousemove', 124))
assert.equal(filled.getColumn('source').getSize(), wideSource + 24, 'wide filled columns can grow from their visible boundary')
pointerDocument.dispatchEvent(pointer('mouseup', 124))
assert.equal(readEquipmentColumnSizing(storage).source, wideSource + 24)
filled.getColumn('source').resetSize()
assert.equal(filled.getColumn('source').getSize(), wideSource)
assert.equal(readEquipmentColumnSizing(storage).source, undefined)

fillViewport = 320
synchronizeFill()
assert.equal(filled.getColumn('source').getSize(), fillDefaults.source, 'narrow windows restore the ordinary last-column width and scroll')
assert.equal(filled.getColumn('currentItem').getSize(), 208)
assert.ok(filled.getTotalSize() > fillViewport)
assert.ok(filled.getLeftTotalSize() <= 192)
filled.resetColumnSizing()
assert.deepEqual(fillPreferences, {})
assert.equal(saved.has(equipmentLayoutStorageKey), false)
fillViewport = 1800
synchronizeFill()
assert.equal(filled.getTotalSize(), fillViewport)
assert.equal(saved.has(equipmentLayoutStorageKey), false, 'reset followed by viewport expansion still leaves storage empty')
console.log('PASS: actual TanStack drag/sticky/keyboard; CJK and proportional Latin predictions; complete-data defaults; persisted reload; safe malformed/blocked storage; single-column/global reset; filter/candidate retention; narrow fixed budget without compressing ordinary columns')
console.log('PASS: full-width final-column fill; visible-width drag including >560px; untouched fill never persisted; zero-movement drag; resize/reset/window transitions preserve canonical preferences')

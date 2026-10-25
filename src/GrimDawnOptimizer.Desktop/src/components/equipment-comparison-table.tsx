import {
  memo,
  useCallback,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from "react";
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnSizingState,
  type Updater,
} from "@tanstack/react-table";
import { RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import type { SlotRow } from "@/lib/types";
import {
  equipmentColumnPinning,
  equipmentColumns,
  equipmentSizingPreferencesAfterChange,
  equipmentVisibleColumnSizing,
  keyboardColumnSize,
  predictEquipmentColumnWidths,
  readEquipmentColumnSizing,
  toEquipmentComparisonRows,
  writeEquipmentColumnSizing,
} from "./equipment-table-model";
import "./equipment-comparison-table.css";

type CellDetail = { title: string; value: string };

const CellText = memo(function CellText({
  value,
  label,
  changed,
  onOpen,
}: {
  value: string;
  label: string;
  changed?: boolean;
  onOpen: (detail: CellDetail, trigger: HTMLButtonElement) => void;
}) {
  if (!value || value === "—")
    return <span className="equipment-cell-empty">—</span>;
  const fullValue = changed ? `${value}\n此槽位有装备或附件变化。` : value;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          className="equipment-cell-text"
          aria-label={`${label}：${value}。查看完整内容`}
          aria-haspopup="dialog"
          onClick={(event) =>
            onOpen({ title: label, value: fullValue }, event.currentTarget)
          }
        >
          <span>{value.replace(/\r?\n/g, " · ")}</span>
          {changed && (
            <span className="equipment-change-marker" aria-label="有变化">
              改
            </span>
          )}
        </button>
      </TooltipTrigger>
      <TooltipContent
        className="equipment-cell-tooltip"
        side="top"
        sideOffset={6}
      >
        {fullValue}
      </TooltipContent>
    </Tooltip>
  );
});

export function EquipmentComparisonTable({
  rows,
  caption,
  sizingRows,
  renderToolbar,
}: {
  rows: SlotRow[];
  caption: string;
  sizingRows?: SlotRow[];
  renderToolbar?: (controls: {
    resetLayout: () => void;
    rowCount: number;
  }) => ReactNode;
}) {
  const tableRef = useRef<HTMLTableElement>(null);
  const returnFocusRef = useRef<HTMLButtonElement | null>(null);
  const tableId = useId();
  const instructionsId = `${tableId}-instructions`;
  const [containerWidth, setContainerWidth] = useState(800);
  const [tableHeight, setTableHeight] = useState(0);
  const [columnSizing, setColumnSizing] = useState<ColumnSizingState>(
    readEquipmentColumnSizing,
  );
  const sizingRef = useRef(columnSizing);
  const [detail, setDetail] = useState<CellDetail | null>(null);
  const data = useMemo(() => toEquipmentComparisonRows(rows), [rows]);
  const predictedWidths = useMemo(
    () => predictEquipmentColumnWidths(sizingRows ?? rows),
    [sizingRows, rows],
  );
  const visibleSizing = useMemo(
    () =>
      equipmentVisibleColumnSizing(
        containerWidth,
        columnSizing,
        predictedWidths,
      ),
    [containerWidth, columnSizing, predictedWidths],
  );
  const layoutRef = useRef({ containerWidth, predictedWidths, visibleSizing });
  layoutRef.current = { containerWidth, predictedWidths, visibleSizing };
  const columns = useMemo(
    () => equipmentColumns(containerWidth, columnSizing, predictedWidths),
    [containerWidth, columnSizing, predictedWidths],
  );

  const updateColumnSizing = useCallback(
    (updater: Updater<ColumnSizingState>) => {
      const layout = layoutRef.current;
      const next = equipmentSizingPreferencesAfterChange(
        sizingRef.current,
        layout.visibleSizing,
        typeof updater === "function" ? updater(layout.visibleSizing) : updater,
      );
      sizingRef.current = next;
      layoutRef.current = {
        ...layout,
        visibleSizing: equipmentVisibleColumnSizing(
          layout.containerWidth,
          next,
          layout.predictedWidths,
        ),
      };
      setColumnSizing(next);
      // Preferences are tiny and written with the user action, so immediately
      // closing or refreshing the app does not lose the final pointer position.
      // Restricted/full storage is deliberately non-blocking.
      writeEquipmentColumnSizing(next);
    },
    [],
  );

  useLayoutEffect(() => {
    const element = tableRef.current;
    const viewport = element?.parentElement;
    if (!element || !viewport) return;
    const measure = () => {
      setContainerWidth(Math.max(1, viewport.clientWidth));
      setTableHeight(element.getBoundingClientRect().height);
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(viewport);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const table = useReactTable({
    data,
    columns,
    getRowId: (row) => row.slot,
    getCoreRowModel: getCoreRowModel(),
    columnResizeMode: "onChange",
    columnResizeDirection: "ltr",
    enableColumnResizing: true,
    autoResetAll: false,
    state: {
      columnSizing: visibleSizing,
      columnPinning: equipmentColumnPinning,
    },
    onColumnSizingChange: updateColumnSizing,
  });

  const openDetail = useCallback(
    (next: CellDetail, trigger: HTMLButtonElement) => {
      returnFocusRef.current = trigger;
      setDetail(next);
    },
    [],
  );

  // Read each TanStack width/offset once per render, then share CSS variables
  // between headers and cells. Sticky offsets therefore track resizing exactly.
  const geometry: Record<string, string | number> = {};
  for (const column of table.getVisibleLeafColumns()) {
    geometry[`--equipment-width-${column.id}`] = `${column.getSize()}px`;
    if (column.getIsPinned() === "left")
      geometry[`--equipment-left-${column.id}`] =
        `${column.getStart("left")}px`;
  }

  const stylesFor = (columnId: string, pinned: boolean): CSSProperties => ({
    width: `var(--equipment-width-${columnId})`,
    minWidth: `var(--equipment-width-${columnId})`,
    maxWidth: `var(--equipment-width-${columnId})`,
    left: pinned ? `var(--equipment-left-${columnId})` : undefined,
  });

  return (
    <section className="equipment-comparison" aria-label={caption}>
      {renderToolbar ? (
        renderToolbar({
          resetLayout: () => table.resetColumnSizing(),
          rowCount: rows.length,
        })
      ) : (
        <div className="equipment-comparison-tools">
          <span>{rows.length} 个槽位 · 前两列固定</span>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => table.resetColumnSizing()}
          >
            <RotateCcw size={13} />
            重置布局
          </Button>
        </div>
      )}
      <p id={instructionsId} className="sr-only">
        槽位和候选装备两列固定在左侧。拖动列标题右侧分隔线调整宽度。聚焦分隔线后，左右方向键调整
        8 像素，Shift 加方向键调整 24 像素，Home 和 End
        调到最小和最大宽度，Enter
        或双击恢复按完整内容预测的列宽。调整后的列宽会在本机记住。聚焦表格后，左右方向键横向滚动。点击或按
        Enter 打开单元格全文。
      </p>
      <div className="equipment-table-frame">
        <Table
          ref={tableRef}
          id={tableId}
          className="equipment-comparison-table"
          style={{ ...geometry, width: table.getTotalSize() } as CSSProperties}
          tabIndex={0}
          aria-describedby={instructionsId}
          onKeyDown={(event) => {
            if (
              event.target !== event.currentTarget ||
              !["ArrowLeft", "ArrowRight"].includes(event.key)
            )
              return;
            event.preventDefault();
            event.currentTarget.parentElement?.scrollBy({
              left: event.key === "ArrowRight" ? 120 : -120,
            });
          }}
        >
          <TableCaption className="sr-only">{caption}</TableCaption>
          <colgroup>
            {table.getVisibleLeafColumns().map((column) => (
              <col
                key={column.id}
                style={{ width: `var(--equipment-width-${column.id})` }}
              />
            ))}
          </colgroup>
          <TableHeader>
            {table.getHeaderGroups().map((group) => (
              <TableRow key={group.id}>
                {group.headers.map((header) => {
                  const column = header.column;
                  const label = String(column.columnDef.header);
                  const minimum = column.columnDef.minSize ?? 20;
                  const maximum = column.columnDef.maxSize ?? 560;
                  return (
                    <TableHead
                      key={header.id}
                      scope="col"
                      style={stylesFor(
                        column.id,
                        column.getIsPinned() === "left",
                      )}
                      data-pinned={column.getIsPinned() || undefined}
                      data-pin-boundary={
                        column.getIsPinned() === "left" &&
                        column.getIsLastColumn("left")
                          ? "true"
                          : undefined
                      }
                    >
                      <span className="equipment-column-label">
                        {flexRender(
                          column.columnDef.header,
                          header.getContext(),
                        )}
                      </span>
                      <div
                        role="separator"
                        tabIndex={0}
                        aria-orientation="vertical"
                        aria-label={`调整${label}列宽`}
                        aria-controls={tableId}
                        aria-describedby={instructionsId}
                        aria-valuenow={Math.round(column.getSize())}
                        aria-valuetext={`${Math.round(column.getSize())} 像素`}
                        aria-valuemin={minimum}
                        aria-valuemax={maximum}
                        className="equipment-column-resizer"
                        data-resizing={
                          column.getIsResizing() ? "true" : undefined
                        }
                        onMouseDown={header.getResizeHandler()}
                        onTouchStart={header.getResizeHandler()}
                        onDoubleClick={() => column.resetSize()}
                        onKeyDown={(event) => {
                          if (event.nativeEvent.isComposing) return;
                          const size = keyboardColumnSize(
                            event.key,
                            column.getSize(),
                            minimum,
                            maximum,
                            column.columnDef.size ?? 150,
                            event.shiftKey,
                          );
                          if (size === undefined) return;
                          event.preventDefault();
                          event.stopPropagation();
                          if (event.key === "Enter") column.resetSize();
                          else
                            table.setColumnSizing((previous) => ({
                              ...previous,
                              [column.id]: size,
                            }));
                        }}
                      />
                    </TableHead>
                  );
                })}
              </TableRow>
            ))}
          </TableHeader>
          <TableBody>
            {table.getRowModel().rows.map((row) => (
              <TableRow
                key={row.id}
                data-changed={row.original.changed ? "true" : undefined}
              >
                {row.getVisibleCells().map((cell) => {
                  const column = cell.column;
                  const isSlot = column.id === "slot";
                  const Cell = isSlot ? TableHead : TableCell;
                  return (
                    <Cell
                      key={cell.id}
                      scope={isSlot ? "row" : undefined}
                      style={stylesFor(
                        column.id,
                        column.getIsPinned() === "left",
                      )}
                      data-column={column.id}
                      data-pinned={column.getIsPinned() || undefined}
                      data-pin-boundary={
                        column.getIsPinned() === "left" &&
                        column.getIsLastColumn("left")
                          ? "true"
                          : undefined
                      }
                    >
                      <CellText
                        value={cell.getValue<string>()}
                        label={`${row.original.slot} · ${String(column.columnDef.header)}`}
                        changed={isSlot && row.original.changed}
                        onOpen={openDetail}
                      />
                    </Cell>
                  );
                })}
              </TableRow>
            ))}
          </TableBody>
        </Table>
        <div
          aria-hidden="true"
          className="equipment-pin-boundary"
          data-overflow={
            table.getTotalSize() > containerWidth + 1 ? "true" : undefined
          }
          style={{ left: table.getLeftTotalSize() - 1, height: tableHeight }}
        />
      </div>
      {!rows.length && (
        <p className="equipment-comparison-empty" role="status">
          没有符合当前筛选的装备槽位。
        </p>
      )}
      <Dialog
        open={detail !== null}
        onOpenChange={(open) => {
          if (!open) setDetail(null);
        }}
      >
        <DialogContent
          className="equipment-cell-dialog"
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            if (returnFocusRef.current?.isConnected)
              returnFocusRef.current.focus({ preventScroll: true });
          }}
        >
          <DialogHeader>
            <DialogTitle>{detail?.title}</DialogTitle>
            <DialogDescription>
              完整名称与来源信息，可选择文字复制。
            </DialogDescription>
          </DialogHeader>
          <div
            className="equipment-cell-fulltext"
            role="region"
            aria-label="单元格完整内容"
            tabIndex={0}
          >
            {detail?.value}
          </div>
        </DialogContent>
      </Dialog>
    </section>
  );
}

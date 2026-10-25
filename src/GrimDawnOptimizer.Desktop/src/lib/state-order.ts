export interface SnapshotCursor {
  sessionId: string;
  revision: number;
}

/** HTTP replies and state events can cross in transit. Retire previous engine
 * sessions too, so a late reply cannot restore the state of a crashed process. */
export class SnapshotOrder {
  private current: SnapshotCursor | null = null;
  private retired = new Set<string>();

  accept(next: SnapshotCursor): boolean {
    if (
      !next.sessionId ||
      !Number.isSafeInteger(next.revision) ||
      next.revision < 1
    )
      return false;
    if (this.retired.has(next.sessionId)) return false;
    if (
      this.current?.sessionId === next.sessionId &&
      next.revision <= this.current.revision
    )
      return false;
    if (this.current && this.current.sessionId !== next.sessionId)
      this.retired.add(this.current.sessionId);
    this.current = { sessionId: next.sessionId, revision: next.revision };
    return true;
  }
}

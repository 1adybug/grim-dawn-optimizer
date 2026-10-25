import { useEffect, useId, useRef, useState } from "react";
import { FolderOpen, LoaderCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { normalizePath, pickPath, type PathKind } from "@/lib/path-picker";
import "./path-input.css";

export interface PathInputProps {
  label: string;
  value: string;
  kind: PathKind;
  disabled?: boolean;
  onCommit: (path: string) => Promise<boolean>;
  commitError?: string;
  placeholder?: string;
  className?: string;
  onBusyChange?: (busy: boolean) => void;
}

export function PathInput({
  label,
  value,
  kind,
  disabled = false,
  onCommit,
  commitError = "路径未能应用，请检查输入后重试。",
  placeholder = "输入完整路径",
  className = "",
  onBusyChange,
}: PathInputProps) {
  const id = useId();
  const [draft, setDraft] = useState(value);
  const [error, setError] = useState("");
  const [invalid, setInvalid] = useState(false);
  const [operation, setOperation] = useState<"browse" | "commit" | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const mounted = useRef(true);
  const locked = useRef(false);
  const draftRef = useRef(value);
  const previousValue = useRef(value);
  const currentValue = useRef(value);
  const currentDisabled = useRef(disabled);
  const syncingPath = useRef<string | null>(null);
  currentValue.current = value;
  currentDisabled.current = disabled;
  const busy = operation !== null;
  const dirty = draft !== value;

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    if (
      draftRef.current === previousValue.current ||
      draftRef.current === syncingPath.current
    ) {
      draftRef.current = value;
      setDraft(value);
      setError("");
      setInvalid(false);
    }
    previousValue.current = value;
    syncingPath.current = null;
  }, [value]);
  useEffect(() => {
    if (invalid && !busy && !disabled) input.current?.focus();
  }, [invalid, busy, disabled]);

  function updateDraft(path: string) {
    draftRef.current = path;
    setDraft(path);
    setError("");
    setInvalid(false);
  }
  function begin(next: "browse" | "commit") {
    if (locked.current || currentDisabled.current) return false;
    locked.current = true;
    setOperation(next);
    onBusyChange?.(true);
    return true;
  }
  function finish() {
    locked.current = false;
    if (mounted.current) {
      setOperation(null);
      onBusyChange?.(false);
    }
  }
  async function persist(path: string) {
    const before = currentValue.current;
    syncingPath.current = path;
    try {
      const ok = await onCommit(path);
      if (!mounted.current) return;
      if (ok) {
        const accepted =
          currentValue.current !== before ? currentValue.current : path;
        syncingPath.current = accepted;
        updateDraft(accepted);
      } else {
        syncingPath.current = null;
        setError(commitError);
        setInvalid(true);
      }
    } catch (cause) {
      if (!mounted.current) return;
      syncingPath.current = null;
      setError(cause instanceof Error ? cause.message : String(cause));
      setInvalid(true);
    }
  }
  async function commit() {
    const selected = normalizePath(draftRef.current);
    if (!selected || !begin("commit")) return;
    // Keep exactly what was typed after a rejected manual submission.
    setError("");
    setInvalid(false);
    try {
      await persist(selected);
    } finally {
      finish();
    }
  }
  async function browse() {
    if (!begin("browse")) return;
    try {
      const selected = await pickPath(
        kind,
        normalizePath(draftRef.current) || value,
      );
      if (!mounted.current || selected === null) return;
      updateDraft(selected);
      if (currentDisabled.current) return;
      setOperation("commit");
      await persist(selected);
    } catch (cause) {
      if (mounted.current) {
        setError(cause instanceof Error ? cause.message : String(cause));
        setInvalid(false);
      }
    } finally {
      finish();
    }
  }

  return (
    <div className={`field path-input ${className}`}>
      <Label htmlFor={id}>{label}</Label>
      <form
        className="path-input-editor"
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          if (dirty) void commit();
        }}
      >
        <Input
          ref={input}
          id={id}
          value={draft}
          disabled={disabled || busy}
          onChange={(event) => {
            syncingPath.current = null;
            updateDraft(event.target.value);
          }}
          onKeyDown={(event) => {
            if (event.key === "Enter" && event.nativeEvent.isComposing)
              event.preventDefault();
          }}
          placeholder={placeholder}
          autoComplete="off"
          spellCheck={false}
          aria-invalid={invalid || undefined}
          aria-describedby={error ? `${id}-error` : undefined}
        />
        <Button
          type="button"
          variant="outline"
          disabled={disabled || busy}
          aria-label={`浏览${label}`}
          aria-busy={operation === "browse"}
          onClick={() => void browse()}
        >
          {operation === "browse" ? (
            <LoaderCircle className="animate-spin" />
          ) : (
            <FolderOpen />
          )}
          浏览
        </Button>
        {dirty && (
          <Button
            type="submit"
            disabled={disabled || busy || !normalizePath(draft)}
            aria-busy={operation === "commit"}
          >
            {operation === "commit" && (
              <LoaderCircle className="animate-spin" />
            )}
            使用
          </Button>
        )}
      </form>
      {error && (
        <p className="field-error" id={`${id}-error`} role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

import { useEffect, useId, useState, type ReactNode } from "react";
import { Info, LoaderCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Empty,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
  EmptyDescription,
  EmptyContent,
} from "@/components/ui/empty";
import {
  Select,
  SelectTrigger,
  SelectContent,
  SelectItem,
  SelectValue,
} from "@/components/ui/select";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "@/components/ui/accordion";

export function SelectField({
  label,
  value,
  options,
  onChange,
  disabled,
  placeholder = "请选择",
}: {
  label: string;
  value: string;
  options: { value: string; label: string }[];
  onChange: (value: string) => void;
  disabled?: boolean;
  placeholder?: string;
}) {
  const id = useId();
  return (
    <div className="field">
      <Label htmlFor={id}>{label}</Label>
      <Select
        value={value || "__empty"}
        onValueChange={(v) => onChange(v === "__empty" ? "" : v)}
        disabled={disabled}
      >
        <SelectTrigger
          id={id}
          className="w-full min-w-0 data-[size=default]:h-9"
        >
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent
          position="popper"
          align="start"
          collisionPadding={8}
          className="field-select-content"
        >
          {options.map((o) => (
            <SelectItem key={o.value || "__empty"} value={o.value || "__empty"}>
              {o.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
export function NumericField({
  label,
  value,
  min,
  max,
  step = 1,
  onCommit,
  disabled,
  integer = false,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  onCommit: (value: number) => void;
  disabled?: boolean;
  integer?: boolean;
}) {
  const id = useId();
  const [draft, setDraft] = useState(String(value));
  const [error, setError] = useState("");
  useEffect(() => {
    setDraft(String(value));
    setError("");
  }, [value]);
  function commit() {
    const n = Number(draft);
    if (
      !draft.trim() ||
      !Number.isFinite(n) ||
      n < min ||
      n > max ||
      (integer && !Number.isInteger(n))
    ) {
      setError(`请输入 ${min}–${max} 之间的${integer ? "整数" : "数值"}`);
      return;
    }
    setError("");
    if (n !== value) onCommit(n);
  }
  return (
    <div className="field">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        className="h-9"
        type="number"
        min={min}
        max={max}
        step={step}
        value={draft}
        disabled={disabled}
        aria-invalid={!!error}
        aria-describedby={error ? `${id}-error` : undefined}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter" && !e.nativeEvent.isComposing) {
            e.preventDefault();
            commit();
          }
        }}
      />
      {error && (
        <span id={`${id}-error`} className="field-error">
          {error}
        </span>
      )}
    </div>
  );
}
export function Disclosure({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <section className="disclosure-section">
      <Accordion type="single" collapsible className="disclosure">
        <AccordionItem value="details">
          <AccordionTrigger>
            <span>
              {title}
              {description && <small>{description}</small>}
            </span>
          </AccordionTrigger>
          <AccordionContent>{children}</AccordionContent>
        </AccordionItem>
      </Accordion>
    </section>
  );
}
export function Notice({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: "neutral" | "warning" | "error";
}) {
  return (
    <Alert
      className={`notice ${tone}`}
      variant={tone === "error" ? "destructive" : "default"}
      role={tone === "error" ? "alert" : undefined}
    >
      <Info size={16} />
      <AlertDescription>{children}</AlertDescription>
    </Alert>
  );
}
export function ActionButton({
  busy,
  children,
  ...props
}: React.ComponentProps<typeof Button> & { busy?: boolean }) {
  return (
    <Button {...props} aria-busy={busy}>
      {busy && <LoaderCircle className="animate-spin" />}
      {children}
    </Button>
  );
}
export function EmptyState({
  icon,
  title,
  children,
  action,
}: {
  icon: ReactNode;
  title: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <Empty className="empty-state">
      <EmptyHeader className="empty-state-header">
        <EmptyMedia variant="icon" className="empty-icon">
          {icon}
        </EmptyMedia>
        <EmptyTitle className="empty-state-title" role="heading" aria-level={2}>
          {title}
        </EmptyTitle>
        <EmptyDescription className="empty-state-description">
          {children}
        </EmptyDescription>
      </EmptyHeader>
      {action && (
        <EmptyContent className="empty-state-actions">{action}</EmptyContent>
      )}
    </Empty>
  );
}

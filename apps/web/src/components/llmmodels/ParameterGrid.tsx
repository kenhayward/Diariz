import ParameterRow from "./ParameterRow";
import { PARAMETERS, type ParameterLayer, type ParameterValue } from "./parameterSchema";

interface Props {
  groupKey: string;
  layer: ParameterLayer;
  /// What each parameter resolves to from the layers below this one - see `resolveInherited`.
  inherited: ParameterLayer;
  onChange: (layer: ParameterLayer) => void;
}

/// One group's thirteen parameters, in two dense columns.
///
/// Source order fills left to right rather than column-major: the pairs that end up side by side
/// (temperature/top_p, max_tokens/max_completion_tokens) are the ones an administrator reasons about
/// together, which column-major fill would separate.
export default function ParameterGrid({ groupKey, layer, inherited, onChange }: Props) {
  function set(key: string, value: ParameterValue) {
    const next = { ...layer };
    // Undefined is not stored as a key - it IS the absence of one. Assigning `next[key] = undefined` would
    // survive into JSON.stringify as a dropped key by luck rather than intent, and would still show up in
    // Object.keys, so delete it explicitly.
    if (value === undefined) delete next[key];
    else next[key] = value;
    onChange(next);
  }

  return (
    <div className="grid grid-cols-1 gap-x-5 md:grid-cols-2">
      {PARAMETERS.map((p) => (
        <ParameterRow
          // Keyed by GROUP and parameter, not parameter alone: the row holds a draft of what is being
          // typed, and a key that is stable across tabs would carry a half-typed value into the call type
          // the admin just switched to.
          key={`${groupKey}-${p.key}`}
          name={p.key}
          label={p.label}
          kind={p.kind}
          min={p.min}
          max={p.max}
          hint={p.hint}
          inherited={inherited[p.key]}
          isBaseGroup={groupKey === "ModelBase"}
          value={layer[p.key]}
          onChange={(v) => set(p.key, v)}
          testId={`param-${groupKey}-${p.key}`}
        />
      ))}
    </div>
  );
}

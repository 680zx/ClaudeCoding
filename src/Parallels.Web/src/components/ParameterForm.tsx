import type { ParameterField, StrategyDescriptor, StrategyParameters } from '../types';

/**
 * Renders a strategy's parameter form from the schema the API serves.
 *
 * There is no per-strategy form component anywhere in this app (spec 4.2):
 * adding a strategy in Contracts grows a form here automatically, which is what
 * "config-driven, never hardcoded" has to mean on the frontend too.
 */
export function ParameterForm({
  descriptor,
  values,
  onChange,
}: {
  descriptor: StrategyDescriptor;
  values: StrategyParameters;
  onChange: (next: StrategyParameters) => void;
}) {
  const update = (field: ParameterField, raw: string) => {
    const parsed = raw === '' ? Number.NaN : Number(raw);
    onChange({ ...values, [field.name]: Number.isNaN(parsed) ? raw : parsed });
  };

  return (
    <div className="param-grid">
      {descriptor.fields.map((field) => (
        <label key={field.name} className="field">
          <span className="field-label">
            {field.label}
            {field.help && <em className="field-help" title={field.help}>?</em>}
          </span>
          <input
            type="number"
            value={String(values[field.name] ?? field.default)}
            min={field.min}
            max={field.max}
            step={field.step ?? (field.kind === 'integer' ? 1 : 0.01)}
            onChange={(e) => update(field, e.target.value)}
          />
        </label>
      ))}
    </div>
  );
}

/** Builds a parameter set from a descriptor's defaults. */
export function defaultsFor(descriptor: StrategyDescriptor): StrategyParameters {
  const values: StrategyParameters = { strategyType: descriptor.strategyType };
  for (const field of descriptor.fields) values[field.name] = field.default;
  return values;
}

/// Shapes shared between the generator (scripts/generate.ts) and the generated module it writes.
/// Kept out of scripts/ so the published package does not need the generator on disk.

export interface GeneratedQueryParam {
  name: string;
  required: boolean;
  description: string;
}

export interface GeneratedOperation {
  value: string;
  displayName: string;
  description: string;
  method: string;
  path: string;
  pathParams: string[];
  queryParams: GeneratedQueryParam[];
  hasBody: boolean;
}

export interface GeneratedResource {
  tag: string;
  displayName: string;
  value: string;
  operations: GeneratedOperation[];
}

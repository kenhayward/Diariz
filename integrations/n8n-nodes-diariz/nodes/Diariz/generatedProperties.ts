import type { IDataObject, INodeProperties, INodePropertyOptions } from "n8n-workflow";
import GENERATED from "./generated";
import type { GeneratedOperation, GeneratedResource } from "./generatedTypes";

export const CUSTOM_API_CALL = "customApiCall";

/// Fills a route template from the values the user supplied. Encodes each value so a stray slash cannot
/// escape into a different route, and refuses a blank rather than sending a literal brace to the server.
export function buildPath(template: string, params: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = params[name];
    if (value === undefined || value === "") {
      throw new Error(`This operation needs a value for "${name}".`);
    }
    return encodeURIComponent(value);
  });
}

export function findGenerated(resource: string, operation: string): GeneratedOperation | undefined {
  return GENERATED.find((r) => r.value === resource)?.operations.find((o) => o.value === operation);
}

export const generatedResources: GeneratedResource[] = GENERATED;

export const generatedResourceOptions: INodePropertyOptions[] = GENERATED.map((r) => ({
  name: r.displayName,
  value: r.value,
  description: `Work with ${r.displayName.toLowerCase()} records`,
}));

/// The Custom API Call fields, shown for every resource. They guarantee that an endpoint added after this
/// version was published is still reachable without waiting for a node release.
export function customApiCallFields(resourceValues: string[]): INodeProperties[] {
  const show = { resource: resourceValues, operation: [CUSTOM_API_CALL] };
  return [
    {
      displayName: "Method",
      name: "customMethod",
      type: "options",
      noDataExpression: true,
      default: "GET",
      options: ["GET", "POST", "PUT", "PATCH", "DELETE"].map((m) => ({ name: m, value: m })),
      displayOptions: { show },
      description: "HTTP method to call the endpoint with",
    },
    {
      displayName: "Path",
      name: "customPath",
      type: "string",
      default: "",
      required: true,
      placeholder: "/api/recordings",
      displayOptions: { show },
      description: "Path on your Diariz server, starting with a slash",
    },
    {
      displayName: "Query Parameters",
      name: "customQuery",
      type: "json",
      default: "{}",
      displayOptions: { show },
      description: "Query string parameters as a JSON object",
    },
    {
      displayName: "Body",
      name: "customBody",
      type: "json",
      default: "{}",
      displayOptions: { show: { ...show, customMethod: ["POST", "PUT", "PATCH", "DELETE"] } },
      description: "Request body as a JSON object",
    },
  ];
}

/// Turns the generated operation list into n8n properties: one operation selector per resource, plus a
/// string field per path parameter and one collection holding the query parameters.
export function buildGeneratedProperties(): INodeProperties[] {
  const properties: INodeProperties[] = [];

  for (const resource of GENERATED) {
    properties.push({
      displayName: "Operation",
      name: "operation",
      type: "options",
      noDataExpression: true,
      displayOptions: { show: { resource: [resource.value] } },
      default: resource.operations[0]?.value ?? CUSTOM_API_CALL,
      options: [
        ...resource.operations.map((o) => ({
          name: o.displayName,
          value: o.value,
          description: o.description,
          action: o.displayName,
        })),
        {
          name: "Custom API Call",
          value: CUSTOM_API_CALL,
          description: "Call any Diariz endpoint directly",
          action: "Custom API call",
        },
      ],
    });

    // One field per distinct path parameter, shown only for the operations that actually use it.
    const byParam = new Map<string, string[]>();
    for (const op of resource.operations) {
      for (const param of op.pathParams) {
        byParam.set(param, [...(byParam.get(param) ?? []), op.value]);
      }
    }
    for (const [param, operations] of byParam) {
      properties.push({
        displayName: titleCase(param),
        name: `path_${param}`,
        type: "string",
        default: "",
        required: true,
        displayOptions: { show: { resource: [resource.value], operation: operations } },
        description: `The ${titleCase(param).toLowerCase()} to act on`,
      });
    }

    const withQuery = resource.operations.filter((o) => o.queryParams.length > 0);
    if (withQuery.length > 0) {
      properties.push({
        displayName: "Query Parameters",
        name: "queryParameters",
        type: "collection",
        placeholder: "Add parameter",
        default: {},
        displayOptions: {
          show: { resource: [resource.value], operation: withQuery.map((o) => o.value) },
        },
        options: distinctQueryOptions(withQuery),
      });
    }

    const withBody = resource.operations.filter((o) => o.hasBody);
    if (withBody.length > 0) {
      properties.push({
        displayName: "Body",
        name: "body",
        type: "json",
        default: "{}",
        displayOptions: { show: { resource: [resource.value], operation: withBody.map((o) => o.value) } },
        description: "Request body as a JSON object. See the API reference for the fields this endpoint takes.",
      });
    }
  }

  return properties;
}

function distinctQueryOptions(operations: GeneratedOperation[]): INodeProperties[] {
  const seen = new Map<string, INodeProperties>();
  for (const op of operations) {
    for (const q of op.queryParams) {
      if (seen.has(q.name)) continue;
      seen.set(q.name, {
        displayName: titleCase(q.name),
        name: q.name,
        type: "string",
        default: "",
        description: q.description,
      });
    }
  }
  return [...seen.values()].sort((a, b) => a.displayName.localeCompare(b.displayName));
}

export function collectPathParams(operation: GeneratedOperation, read: (name: string) => string): Record<string, string> {
  const values: Record<string, string> = {};
  for (const param of operation.pathParams) values[param] = read(`path_${param}`);
  return values;
}

export function asDataObject(value: unknown): IDataObject | undefined {
  if (value === undefined || value === null || value === "") return undefined;
  if (typeof value === "string") {
    const trimmed = value.trim();
    if (trimmed === "" || trimmed === "{}") return undefined;
    return JSON.parse(trimmed) as IDataObject;
  }
  return value as IDataObject;
}

function titleCase(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return spaced[0].toUpperCase() + spaced.slice(1);
}

import type {
  IDataObject,
  IExecuteFunctions,
  IHttpRequestMethods,
  INodeExecutionData,
  INodeType,
  INodeTypeDescription,
} from "n8n-workflow";
import { NodeOperationError } from "n8n-workflow";
import { diarizApiRequest } from "./transport/request";
import { applyLimit } from "./transport/pagination";
import { pollUntilTerminal } from "./transport/poll";
import { accumulateSse } from "./transport/sse";
import { BINARY_DOWNLOADS, BINARY_UPLOADS, key, SSE_OPERATIONS, WAIT_OPERATIONS } from "./enhancements";
import type { WaitOperation } from "./enhancements";
import * as loadOptionsMethods from "./methods/loadOptions";
import {
  asDataObject,
  buildGeneratedProperties,
  buildPath,
  collectPathParams,
  customApiCallFields,
  CUSTOM_API_CALL,
  findGenerated,
  generatedResourceOptions,
  generatedResources,
} from "./generatedProperties";

/// The Diariz action node. Its operations are generated from the platform's own OpenAPI document, so the
/// whole REST surface is reachable, and every resource additionally offers a Custom API Call for anything
/// added after this version was published.
export class Diariz implements INodeType {
  description: INodeTypeDescription = {
    displayName: "Diariz",
    name: "diariz",
    icon: "file:diariz.svg",
    group: ["transform"],
    version: 1,
    subtitle: '={{$parameter["operation"] + ": " + $parameter["resource"]}}',
    description: "Work with your Diariz recordings, transcripts, formulas and more",
    defaults: { name: "Diariz" },
    inputs: ["main"],
    outputs: ["main"],
    credentials: [{ name: "diarizApi", required: true }],
    properties: [
      {
        displayName: "Resource",
        name: "resource",
        type: "options",
        noDataExpression: true,
        // A literal, not an expression off the generated list: the n8n linter reads defaults statically.
        // test/node.test.ts asserts it stays a real resource value.
        default: "recordings",
        options: generatedResourceOptions,
      },
      ...buildGeneratedProperties(),
      ...customApiCallFields(generatedResources.map((r) => r.value)),
    ],
  };

  methods = { loadOptions: loadOptionsMethods };

  async execute(this: IExecuteFunctions): Promise<INodeExecutionData[][]> {
    const items = this.getInputData();
    const out: INodeExecutionData[] = [];

    for (let i = 0; i < items.length; i++) {
      try {
        const resource = this.getNodeParameter("resource", i) as string;
        const operation = this.getNodeParameter("operation", i) as string;

        let response: unknown;

        if (operation === CUSTOM_API_CALL) {
          const method = this.getNodeParameter("customMethod", i) as IHttpRequestMethods;
          const path = this.getNodeParameter("customPath", i) as string;
          const qs = asDataObject(this.getNodeParameter("customQuery", i, "{}"));
          const body =
            method === "GET" ? undefined : asDataObject(this.getNodeParameter("customBody", i, "{}"));
          response = await diarizApiRequest.call(this, method, path, body, qs);
        } else {
          const generated = findGenerated(resource, operation);
          if (!generated) {
            throw new NodeOperationError(
              this.getNode(),
              `Unknown operation "${operation}" on resource "${resource}".`,
              { itemIndex: i },
            );
          }

          const params = collectPathParams(generated, (name) => this.getNodeParameter(name, i, "") as string);
          const path = buildPath(generated.path, params);
          const qs =
            generated.queryParams.length > 0
              ? (this.getNodeParameter("queryParameters", i, {}) as IDataObject)
              : undefined;
          const enhancement = key(resource, operation);

          const download = BINARY_DOWNLOADS[enhancement];
          if (download) {
            const binaryProperty = this.getNodeParameter("binaryPropertyName", i, "data") as string;
            const file = (await diarizApiRequest.call(
              this,
              generated.method as IHttpRequestMethods,
              path,
              undefined,
              qs,
              { json: false, encoding: "arraybuffer" },
            )) as Buffer;
            out.push({
              json: { fileName: download.fileName },
              binary: {
                [binaryProperty]: await this.helpers.prepareBinaryData(Buffer.from(file), download.fileName),
              },
              pairedItem: { item: i },
            });
            continue;
          }

          const upload = BINARY_UPLOADS[enhancement];
          if (upload) {
            const binaryProperty = this.getNodeParameter("binaryPropertyName", i, "data") as string;
            const binary = this.helpers.assertBinaryData(i, binaryProperty);
            const buffer = await this.helpers.getBinaryDataBuffer(i, binaryProperty);

            const form = new FormData();
            form.append(
              upload.field,
              new Blob([new Uint8Array(buffer)], { type: binary.mimeType || "application/octet-stream" }),
              binary.fileName ?? "upload",
            );
            for (const [name, value] of Object.entries(upload.fixedFields ?? {})) form.append(name, value);
            const extra = this.getNodeParameter("uploadOptions", i, {}) as IDataObject;
            for (const [name, value] of Object.entries(extra)) {
              if (value !== undefined && value !== "") form.append(name, String(value));
            }

            response = await diarizApiRequest.call(
              this,
              generated.method as IHttpRequestMethods,
              path,
              undefined,
              undefined,
              { body: form, json: false },
            );
            response = typeof response === "string" ? safeParse(response) : response;
          } else if (SSE_OPERATIONS.includes(enhancement)) {
            response = await askChat.call(this, i);
          } else {
            const body = generated.hasBody ? asDataObject(this.getNodeParameter("body", i, "{}")) : undefined;
            response = await diarizApiRequest.call(
              this,
              generated.method as IHttpRequestMethods,
              path,
              body,
              qs && Object.keys(qs).length > 0 ? qs : undefined,
            );

            const wait = WAIT_OPERATIONS[enhancement];
            if (wait && (this.getNodeParameter("waitForCompletion", i, true) as boolean)) {
              response = await waitForResult.call(this, i, wait, response as IDataObject, params);
            }
          }
        }

        // A list endpoint returns an array; emit one n8n item per element so downstream nodes iterate.
        if (Array.isArray(response)) {
          const returnAll = this.getNodeParameter("returnAll", i, true) as boolean;
          const limit = this.getNodeParameter("limit", i, 50) as number;
          const limited = applyLimit(response, returnAll, limit);
          out.push(...limited.map((entry) => ({ json: entry as IDataObject, pairedItem: { item: i } })));
        } else if (response === undefined || response === null || response === "") {
          out.push({ json: { success: true }, pairedItem: { item: i } });
        } else if (typeof response === "object") {
          out.push({ json: response as IDataObject, pairedItem: { item: i } });
        } else {
          out.push({ json: { data: response }, pairedItem: { item: i } });
        }
      } catch (error) {
        if (this.continueOnFail()) {
          out.push({ json: { error: (error as Error).message }, pairedItem: { item: i } });
          continue;
        }
        throw error;
      }
    }

    return [out];
  }
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return { data: text };
  }
}

/// Diariz answers a formula run with 202 and a document still in Generating. A workflow author almost
/// always wants the finished text in the same node, so poll the nested result until it settles.
async function waitForResult(
  this: IExecuteFunctions,
  itemIndex: number,
  wait: WaitOperation,
  started: IDataObject,
  pathParams: Record<string, string>,
): Promise<IDataObject> {
  const resultId = String(started.id ?? "");
  const recordingId = pathParams.recordingId ?? String(started.recordingId ?? "");
  if (!resultId || !recordingId) return started;

  const pollPath = wait.pollPath
    .replace("{recordingId}", encodeURIComponent(recordingId))
    .replace("{id}", encodeURIComponent(resultId));

  const intervalMs = (this.getNodeParameter("pollIntervalSeconds", itemIndex, 3) as number) * 1000;
  const timeoutMs = (this.getNodeParameter("timeoutSeconds", itemIndex, 300) as number) * 1000;

  try {
    return await pollUntilTerminal<IDataObject>(
      async () => (await diarizApiRequest.call(this, "GET", pollPath)) as IDataObject,
      (value) => {
        const status = String(value[wait.statusField] ?? "");
        if (status === wait.readyValue) return "ready";
        if (status === wait.failedValue) return "failed";
        return "pending";
      },
      { intervalMs, timeoutMs },
    );
  } catch (error) {
    // Name the document so the workflow can still fetch it later rather than losing the run entirely.
    throw new NodeOperationError(
      this.getNode(),
      `${(error as Error).message} (formula result ID ${resultId} on recording ${recordingId})`,
      { itemIndex },
    );
  }
}

/// Chat is server-sent events. Consume the stream here so the workflow sees one finished answer.
async function askChat(this: IExecuteFunctions, itemIndex: number): Promise<IDataObject> {
  const question = this.getNodeParameter("chatQuestion", itemIndex) as string;
  const ids = (this.getNodeParameter("chatRecordingIds", itemIndex, "") as string)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);

  const stream = (await diarizApiRequest.call(
    this,
    "POST",
    "/api/chat/stream",
    { message: question, recordingIds: ids },
    undefined,
    { json: false, returnFullResponse: false, encoding: "text" },
  )) as string;

  const result = await accumulateSse((async function* () {
    yield stream;
  })());

  return { answer: result.answer, references: result.references, model: result.model };
}

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
          const body = generated.hasBody ? asDataObject(this.getNodeParameter("body", i, "{}")) : undefined;

          response = await diarizApiRequest.call(
            this,
            generated.method as IHttpRequestMethods,
            path,
            body,
            qs && Object.keys(qs).length > 0 ? qs : undefined,
          );
        }

        // A list endpoint returns an array; emit one n8n item per element so downstream nodes iterate.
        if (Array.isArray(response)) {
          out.push(...response.map((entry) => ({ json: entry as IDataObject, pairedItem: { item: i } })));
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

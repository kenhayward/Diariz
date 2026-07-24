import type {
  IDataObject,
  IHookFunctions,
  INodeType,
  INodeTypeDescription,
  IWebhookFunctions,
  IWebhookResponseData,
} from "n8n-workflow";
import { NodeOperationError } from "n8n-workflow";
import { diarizApiRequest } from "./transport/request";
import { verifyWebhookSignature } from "./signature";
import { EVENT_OPTIONS } from "./events";

/// A self-registering webhook trigger. On activation it creates its own subscription in Diariz through
/// /api/user/webhooks and stores the returned signing secret (Diariz returns it exactly once); on
/// deactivation it deletes the subscription, so the per-user cap never fills with orphans.
export class DiarizTrigger implements INodeType {
  description: INodeTypeDescription = {
    displayName: "Diariz Trigger",
    name: "diarizTrigger",
    icon: "file:diariz.svg",
    group: ["trigger"],
    version: 1,
    subtitle: '={{$parameter["events"].join(", ")}}',
    description: "Starts a workflow when something happens in Diariz",
    defaults: { name: "Diariz Trigger" },
    inputs: [],
    outputs: ["main"],
    credentials: [{ name: "diarizApi", required: true }],
    webhooks: [
      {
        name: "default",
        httpMethod: "POST",
        responseMode: "onReceived",
        path: "webhook",
        // Required: the signature covers the exact bytes Diariz sent, and a re-serialised body never matches.
        rawBody: true,
      },
    ],
    properties: [
      {
        displayName: "Events",
        name: "events",
        type: "multiOptions",
        required: true,
        default: [],
        options: EVENT_OPTIONS,
        description: "Which Diariz events start this workflow",
      },
      {
        displayName: "Simplify",
        name: "simplify",
        type: "boolean",
        default: true,
        description:
          "Whether to return only the event data instead of the full event envelope with its ID, type and timestamp",
      },
    ],
  };

  webhookMethods = {
    default: {
      async checkExists(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        // Without the secret we cannot verify deliveries, and Diariz only ever returns it once. Treat a
        // secret-less subscription as absent so create() replaces it rather than leaving it unverifiable.
        if (!data.subscriptionId || !data.secret) return false;

        const url = this.getNodeWebhookUrl("default");
        const existing = (await diarizApiRequest.call(this, "GET", "/api/user/webhooks")) as IDataObject[];
        return existing.some((s) => s.id === data.subscriptionId && s.url === url);
      },

      async create(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        const url = this.getNodeWebhookUrl("default") as string;
        const events = this.getNodeParameter("events") as string[];
        if (!events || events.length === 0) {
          throw new NodeOperationError(this.getNode(), "Choose at least one event before activating.");
        }

        // A leftover subscription on this URL would double-deliver, so clear any first.
        const existing = (await diarizApiRequest.call(this, "GET", "/api/user/webhooks")) as IDataObject[];
        for (const stale of existing.filter((s) => s.url === url)) {
          await diarizApiRequest.call(this, "DELETE", `/api/user/webhooks/${stale.id}`);
        }

        const created = (await diarizApiRequest.call(this, "POST", "/api/user/webhooks", {
          name: `n8n: ${this.getWorkflow().name ?? "workflow"}`,
          url,
          eventTypes: events,
        })) as IDataObject;

        data.subscriptionId = created.id;
        data.secret = created.secret; // returned exactly once by Diariz
        return true;
      },

      async delete(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        if (data.subscriptionId) {
          await diarizApiRequest.call(this, "DELETE", `/api/user/webhooks/${data.subscriptionId}`);
        }
        delete data.subscriptionId;
        delete data.secret;
        return true;
      },
    },
  };

  async webhook(this: IWebhookFunctions): Promise<IWebhookResponseData> {
    const data = this.getWorkflowStaticData("node");
    const req = this.getRequestObject() as unknown as { rawBody?: Buffer | string };
    const raw = typeof req.rawBody === "string" ? req.rawBody : (req.rawBody?.toString("utf8") ?? "");

    const verified = verifyWebhookSignature({
      secret: (data.secret as string) ?? "",
      headers: this.getHeaderData() as Record<string, string | string[] | undefined>,
      rawBody: raw,
    });
    if (!verified.ok) {
      // 401 and no items. Diariz retries on its own backoff schedule, which is the right behaviour for a
      // genuinely misconfigured secret and harmless for a forged request.
      return { webhookResponse: { status: 401, body: verified.reason } };
    }

    let envelope: IDataObject;
    try {
      envelope = JSON.parse(raw) as IDataObject;
    } catch {
      return { webhookResponse: { status: 400, body: "The request body was not valid JSON." } };
    }

    const simplify = this.getNodeParameter("simplify", true) as boolean;
    const json = simplify ? ((envelope.data as IDataObject) ?? envelope) : envelope;

    return { workflowData: [this.helpers.returnJsonArray([json])] };
  }
}

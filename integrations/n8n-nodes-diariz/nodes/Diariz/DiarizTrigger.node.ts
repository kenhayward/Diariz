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
import { EVENT_OPTIONS, PLATFORM_EVENT_OPTIONS, SIGNAL_EXEMPT_EVENTS } from "./events";
import { getWorkflowSignals } from "./methods/loadOptions";

/// The two subscription scopes Diariz offers, and the endpoint each one registers through. Personal is the
/// default and the only one that existed before 0.177.0, so a node saved without a Scope keeps its old
/// behaviour exactly.
const PERSONAL_PATH = "/api/user/webhooks";
const PLATFORM_PATH = "/api/admin/webhooks";

/// A self-registering webhook trigger. On activation it creates its own subscription in Diariz and stores
/// the returned signing secret (Diariz returns it exactly once); on deactivation it deletes the
/// subscription, so the cap never fills with orphans.
///
/// Two scopes, because Diariz models them as different things rather than as one thing with a permission
/// on it. A PERSONAL subscription (/api/user/webhooks, any signed-in user) carries events about the
/// credential owner's own recordings. A PLATFORM subscription (/api/admin/webhooks, Platform Administrator
/// only) fires across all users and can additionally carry platform-only events such as
/// feedback.submitted, which no personal subscription may ever receive - it would deliver one user's words
/// to another.
export class DiarizTrigger implements INodeType {
  description: INodeTypeDescription = {
    displayName: "Diariz Trigger",
    name: "diarizTrigger",
    icon: "file:diariz.svg",
    group: ["trigger"],
    version: 1,
    subtitle:
      '={{ ($parameter["scope"] === "platform" ? $parameter["platformEvents"] : $parameter["events"]).join(", ") }}',
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
        displayName: "Scope",
        name: "scope",
        type: "options",
        default: "personal",
        options: [
          {
            name: "Personal",
            value: "personal",
            description: "Events about your own recordings. Works with any Diariz account.",
          },
          {
            name: "Platform (Administrator)",
            value: "platform",
            description:
              "Events across every user, routed by Workflow Signal, plus platform-only events such as Feedback Received. The credential must belong to a Platform Administrator.",
          },
        ],
        description: "Whose events this workflow listens to",
      },
      {
        displayName: "Events",
        name: "events",
        type: "multiOptions",
        required: true,
        default: [],
        options: EVENT_OPTIONS,
        displayOptions: { show: { scope: ["personal"] } },
        description: "Which Diariz events start this workflow",
      },
      {
        // A separate parameter rather than a longer list on "events", so a platform-only event can never be
        // picked in Personal scope - Diariz rejects that combination and the workflow would fail to activate.
        displayName: "Events",
        name: "platformEvents",
        type: "multiOptions",
        required: true,
        default: [],
        options: PLATFORM_EVENT_OPTIONS,
        displayOptions: { show: { scope: ["platform"] } },
        description: "Which Diariz events start this workflow",
      },
      {
        displayName: "Signal Filter Names or IDs",
        name: "signalFilter",
        type: "multiOptions",
        default: [],
        typeOptions: { loadOptionsMethod: "getWorkflowSignals" },
        displayOptions: { show: { scope: ["platform"] } },
        description:
          'Which Workflow Signals this subscription routes on. A platform automation is broad-reach, so signals are what scope it, and Diariz rejects one with no signal - except for Feedback Received, which carries no signal and always fires. Choose from the list, or specify IDs using an <a href="https://docs.n8n.io/code/expressions/">expression</a>.',
      },
      {
        displayName: "Simplify",
        name: "simplify",
        type: "boolean",
        default: true,
        description:
          "Whether to return only the event data instead of the full event envelope with its ID, type and timestamp",
      },
      {
        // Personal only: the platform create endpoint takes no attendee-contacts flag, so showing this in
        // Platform scope would be a control that silently does nothing.
        displayName: "Include Attendee Contacts",
        name: "includeAttendeeContacts",
        type: "boolean",
        default: false,
        displayOptions: { show: { scope: ["personal"] } },
        description:
          "Whether to include each attendee's job title, company, email address and phone number in the event. Turn this on to route email straight from the payload. It sends personal data to this webhook URL, so it is off unless you ask for it.",
      },
      {
        displayName: "Include Feedback Text",
        name: "includeFeedbackText",
        type: "boolean",
        default: false,
        displayOptions: { show: { scope: ["platform"] } },
        description:
          "Whether to include what the person actually wrote in a Feedback Received event. It is free text and may quote meeting content, so it is off unless you ask for it. With it off you still get who sent it, the page they were on and the release.",
      },
    ],
  };

  // The signal filter is a picker over /api/workflow-signals, so the trigger needs loadOptions too.
  methods = { loadOptions: { getWorkflowSignals } };

  webhookMethods = {
    default: {
      async checkExists(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        // Without the secret we cannot verify deliveries, and Diariz only ever returns it once. Treat a
        // secret-less subscription as absent so create() replaces it rather than leaving it unverifiable.
        if (!data.subscriptionId || !data.secret) return false;

        const scope = this.getNodeParameter("scope", "personal") as string;
        const platform = scope === "platform";
        // A scope change has to force a rebuild: the subscription lives at a different endpoint entirely,
        // so the old one would keep delivering under the old scope while the node believes it is current.
        if ((data.scope ?? "personal") !== scope) return false;

        const url = this.getNodeWebhookUrl("default");
        const wantsContacts = this.getNodeParameter("includeAttendeeContacts", false) as boolean;
        const wantsFeedbackText = this.getNodeParameter("includeFeedbackText", false) as boolean;
        const existing = (await diarizApiRequest.call(
          this,
          "GET",
          platform ? PLATFORM_PATH : PERSONAL_PATH,
        )) as IDataObject[];

        // The opt-in settings have to be compared, not just the identity. create() is the only place that can
        // re-apply them, and n8n calls create() only when this says no - so without the comparison, turning an
        // option on would do nothing to a subscription that already exists.
        return existing.some(
          (s) =>
            s.id === data.subscriptionId &&
            s.url === url &&
            (platform
              ? (s.includeFeedbackText ?? false) === wantsFeedbackText
              : (s.includeAttendeeContacts ?? false) === wantsContacts),
        );
      },

      async create(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        const url = this.getNodeWebhookUrl("default") as string;
        const scope = this.getNodeParameter("scope", "personal") as string;
        const platform = scope === "platform";
        const path = platform ? PLATFORM_PATH : PERSONAL_PATH;

        const events = this.getNodeParameter(platform ? "platformEvents" : "events") as string[];
        if (!events || events.length === 0) {
          throw new NodeOperationError(this.getNode(), "Choose at least one event before activating.");
        }

        const signals = platform ? ((this.getNodeParameter("signalFilter", []) as string[]) ?? []) : [];
        // Checked here rather than left to the API so the message names the cause. Diariz requires a signal
        // for every signal-routed event; the exempt ones (Feedback Received) fire whatever the filter says,
        // so a subscription made only of those needs none.
        if (platform && signals.length === 0 && events.some((e) => !SIGNAL_EXEMPT_EVENTS.includes(e))) {
          throw new NodeOperationError(
            this.getNode(),
            "Choose at least one Workflow Signal. A platform automation is broad-reach, so signals are what scope it, and every event here except Feedback Received needs one.",
          );
        }

        // A leftover subscription on this URL would double-deliver, so clear any first.
        const existing = (await diarizApiRequest.call(this, "GET", path)) as IDataObject[];
        for (const stale of existing.filter((s) => s.url === url)) {
          await diarizApiRequest.call(this, "DELETE", `${path}/${stale.id}`);
        }

        // Sent on every registration, including the re-registration a re-publish causes. This node owns the
        // subscription's whole lifecycle, so a setting held only on the Diariz side is wiped the next time the
        // workflow is edited - which is why the choice lives on the node rather than in Diariz.
        const name = `n8n: ${this.getWorkflow().name ?? "workflow"}`;
        const body: IDataObject = platform
          ? {
              name,
              url,
              eventTypes: events,
              signalFilter: signals,
              includeFeedbackText: this.getNodeParameter("includeFeedbackText", false) as boolean,
            }
          : {
              name,
              url,
              eventTypes: events,
              includeAttendeeContacts: this.getNodeParameter("includeAttendeeContacts", false) as boolean,
            };
        const created = (await diarizApiRequest.call(this, "POST", path, body)) as IDataObject;

        data.subscriptionId = created.id;
        data.secret = created.secret; // returned exactly once by Diariz
        data.scope = scope; // so checkExists can spot a scope change and rebuild at the right endpoint
        return true;
      },

      async delete(this: IHookFunctions): Promise<boolean> {
        const data = this.getWorkflowStaticData("node");
        if (data.subscriptionId) {
          // The scope RECORDED AT CREATION, not the one currently selected: after a scope change the
          // subscription still lives where it was made, and deleting from the new endpoint would 404 and
          // strand it delivering forever.
          const path = (data.scope ?? "personal") === "platform" ? PLATFORM_PATH : PERSONAL_PATH;
          await diarizApiRequest.call(this, "DELETE", `${path}/${data.subscriptionId}`);
        }
        delete data.subscriptionId;
        delete data.secret;
        delete data.scope;
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

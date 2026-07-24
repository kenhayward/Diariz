import type {
  IAuthenticateGeneric,
  ICredentialTestRequest,
  ICredentialType,
  INodeProperties,
} from "n8n-workflow";

/// A personal Diariz API token plus the address of the server it belongs to. The credential test hits
/// /api/user/profile, which conveniently also reports whether the platform administrator has enabled
/// API access and Automations - so a misconfigured instance is diagnosed at save time rather than as a
/// bare 403 during the first execution.
export class DiarizApi implements ICredentialType {
  name = "diarizApi";
  displayName = "Diariz API";
  documentationUrl = "https://github.com/kenhayward/Diariz/tree/main/integrations/n8n-nodes-diariz";

  properties: INodeProperties[] = [
    {
      displayName: "Base URL",
      name: "baseUrl",
      type: "string",
      default: "",
      required: true,
      placeholder: "https://diariz.example.com",
      description: "The address of your Diariz server, with no trailing path",
    },
    {
      displayName: "API Token",
      name: "apiToken",
      type: "string",
      typeOptions: { password: true },
      default: "",
      required: true,
      description:
        "A personal API token (dz_api_...) from Settings > Developers. A read-only token blocks every write operation, and a token with an expiry stops working on that date.",
    },
  ];

  authenticate: IAuthenticateGeneric = {
    type: "generic",
    properties: {
      headers: { Authorization: "=Bearer {{$credentials.apiToken}}" },
    },
  };

  test: ICredentialTestRequest = {
    request: {
      baseURL: "={{$credentials.baseUrl.replace(new RegExp('/+$'), '')}}",
      url: "/api/user/profile",
    },
    rules: [
      {
        type: "responseSuccessBody",
        properties: {
          key: "apiAccessEnabled",
          value: false,
          message:
            "Your token is valid, but API access is turned off on this Diariz instance. Ask your platform administrator to enable it in Settings.",
        },
      },
      {
        type: "responseSuccessBody",
        properties: {
          key: "webhooksEnabled",
          value: false,
          message:
            "Your token works and action nodes will run, but Automations are turned off, so the Diariz Trigger cannot activate. Ask your platform administrator to enable Automations.",
        },
      },
    ],
  };
}

import type {
  IDataObject,
  IExecuteFunctions,
  IHookFunctions,
  IHttpRequestMethods,
  IHttpRequestOptions,
  ILoadOptionsFunctions,
} from "n8n-workflow";
import { NodeApiError } from "n8n-workflow";

export type DiarizContext = IExecuteFunctions | ILoadOptionsFunctions | IHookFunctions;

/// Joins the credential's base URL to an API path, tolerating trailing slashes the user pasted in.
export function joinUrl(baseUrl: string, path: string): string {
  return `${baseUrl.replace(/\/+$/, "")}${path}`;
}

/// Turns a Diariz HTTP failure into a sentence a workflow author can act on. Pure, so it is unit-tested.
export function describeError(status: number, body: unknown): string {
  const fromBody =
    typeof body === "string" && body.trim().length > 0
      ? body.trim()
      : typeof body === "object" && body !== null
        ? (((body as IDataObject).title as string) ?? ((body as IDataObject).message as string) ?? "")
        : "";

  if (status === 401) {
    return "Diariz rejected the token. It may have expired or been revoked - check Settings > Developers, and remember that a token can carry an expiry date.";
  }
  if (status === 403) {
    return (
      fromBody ||
      "Diariz refused this request. The capability may be turned off by your platform administrator, or your token may be read-only."
    );
  }
  if (status === 404) {
    return (
      fromBody ||
      "Diariz could not find that item. It may have been deleted, or it may belong to another account."
    );
  }
  return fromBody || `Diariz returned HTTP ${status}.`;
}

/// The single HTTP entry point for every node operation. Nothing calls httpRequest directly, so credential
/// handling and error wording stay in one place.
export async function diarizApiRequest(
  this: DiarizContext,
  method: IHttpRequestMethods,
  path: string,
  body?: IDataObject,
  qs?: IDataObject,
  options: Partial<IHttpRequestOptions> = {},
): Promise<any> {
  const { baseUrl } = (await this.getCredentials("diarizApi")) as { baseUrl: string };

  const request: IHttpRequestOptions = {
    method,
    url: joinUrl(baseUrl, path),
    json: true,
    ...options,
  };
  if (body !== undefined) request.body = body;
  if (qs !== undefined) request.qs = qs;

  try {
    return await this.helpers.httpRequestWithAuthentication.call(this, "diarizApi", request);
  } catch (error) {
    const err = error as {
      httpCode?: string | number;
      statusCode?: number;
      response?: { body?: unknown };
      error?: unknown;
    };
    const status = Number(err.httpCode ?? err.statusCode ?? 0);
    throw new NodeApiError(this.getNode(), error as never, {
      message: describeError(status, err.response?.body ?? err.error),
      httpCode: String(status),
    });
  }
}

import { AUDIO_EXTENSIONS, CONTAINER_EXTENSIONS } from "./mediaKinds";

/**
 * Naming the file dialog's filter.
 *
 * `<input accept="...">` offers no control over the label the browser writes above its filter dropdown;
 * Chromium calls it "Custom Files" and there is no HTML for changing that. `showOpenFilePicker` can name
 * it, through `types[].description`, but MDN marks the API "not Baseline" - Chromium implements it,
 * Firefox and Safari do not.
 *
 * So this is a progressive enhancement and nothing more: where the API exists the dialog says
 * "Audio and video files", and everywhere else the caller opens the ordinary `<input>` exactly as before.
 * Any failure returns null so the caller falls back rather than leaving the button doing nothing.
 */
export interface MediaPickerType {
  description: string;
  accept: Record<string, string[]>;
}

const dotted = (exts: readonly string[]) => exts.map((e) => `.${e}`);

export const MEDIA_PICKER_TYPES: MediaPickerType[] = [
  {
    description: "Audio and video files",
    accept: {
      "audio/*": dotted(AUDIO_EXTENSIONS),
      "video/*": dotted(CONTAINER_EXTENSIONS),
    },
  },
];

interface FileSystemFileHandleLike {
  getFile(): Promise<File>;
}

type ShowOpenFilePicker = (opts: {
  multiple?: boolean;
  excludeAcceptAllOption?: boolean;
  types?: MediaPickerType[];
}) => Promise<FileSystemFileHandleLike[]>;

/**
 * Open the named picker. Returns the chosen files, `[]` if the user cancelled, or **null** if the
 * picker is unavailable or refused - in which case the caller should click the hidden `<input>`.
 */
export async function pickMediaFiles(): Promise<File[] | null> {
  const show = (globalThis as unknown as { showOpenFilePicker?: ShowOpenFilePicker })
    .showOpenFilePicker;
  if (typeof show !== "function") return null;

  try {
    const handles = await show({
      multiple: true,
      // Keep "All files" available: the extension list is a convenience, and the bytes are sniffed by
      // the extractor and again by the server, so a correct file with an odd name should not be
      // unpickable.
      excludeAcceptAllOption: false,
      types: MEDIA_PICKER_TYPES,
    });
    return await Promise.all(handles.map((h) => h.getFile()));
  } catch (err) {
    // Dismissing the dialog is a choice, not a failure.
    if (err instanceof Error && err.name === "AbortError") return [];
    return null;
  }
}

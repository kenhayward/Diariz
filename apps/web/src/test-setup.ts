// Initialise i18next (side-effect) before any test renders a component that calls useTranslation, and
// pin the language to English so assertions against English UI text are stable.
import i18n from "./lib/i18n";

i18n.changeLanguage("en");

// jsdom doesn't implement Element.scrollIntoView; components that scroll to a segment (RecordingDetail's
// transcript deep-link) call it in an effect, which otherwise throws an unhandled error and fails the run.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}

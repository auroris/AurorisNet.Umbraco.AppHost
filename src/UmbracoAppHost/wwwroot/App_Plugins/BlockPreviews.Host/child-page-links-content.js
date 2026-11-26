import { heading, note } from "../ApplicationBlocks/preview-model.js";

export function childPageLinksPreview(content) {
    return (content.heading ? heading(content.heading) : "")
        + note("Displays cards linking to this page's visible child pages.");
}

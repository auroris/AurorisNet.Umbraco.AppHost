import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { memberSignInPreview } from "./member-sign-in-content.js";
export { memberSignInPreview };

export default class MemberSignInPreview extends BlockPreview {
    renderContent(alias, content) { return memberSignInPreview(content); }
}
customElements.define("app-member-sign-in-preview", MemberSignInPreview);

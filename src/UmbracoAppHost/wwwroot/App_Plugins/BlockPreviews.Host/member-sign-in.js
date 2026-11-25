import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { button, heading, note } from "../ApplicationBlocks/preview-model.js";

export function memberSignInPreview(content) {
    const automatic = [true, 1, "1", "true"].includes(content.automaticSignIn);
    return heading(content.heading || "Sign in")
        + '<label>Username<input disabled></label><label>Password<input type="password" disabled></label>'
        + button("Sign in")
        + note(automatic ? "Automatically starts the configured member sign-in provider for anonymous visitors." : "Visible to visitors who need to sign in as a member.");
}

export default class MemberSignInPreview extends BlockPreview {
    renderContent(alias, content) { return memberSignInPreview(content); }
}
customElements.define("app-member-sign-in-preview", MemberSignInPreview);

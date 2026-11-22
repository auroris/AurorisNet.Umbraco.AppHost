import { mountAccountPicker, mountAccountSearch } from "../ApplicationBlocks/account-picker.js";

const keyOf = person => person.sid;

export function mountPeoplePicker(box) {
    mountAccountPicker(box, { popupUrl: new URL("people-picker.html", import.meta.url),
        keyParameter: "sid", keyOf, blockAlias: "appPeoplePicker" });
}

export function mountPeopleSearch() {
    mountAccountSearch({ keyOf, detailOf: person => person.email, emptyMessage: "No matching people. Try another name." });
}

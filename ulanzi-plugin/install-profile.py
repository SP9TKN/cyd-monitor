"""Puts the SDR Oxide actions on the Ulanzi Dial's preset, over the original kept in backup.

There is one preset on purpose: the server decides per event whether the dial is Oxide's or
the system's, see DialBridge.cs.

Close Ulanzi Studio first: it holds the presets in memory and writes them back.
"""
import json
import os
import subprocess
import sys
import uuid

PAGE = os.path.expandvars(
    r"%APPDATA%\Ulanzi\UlanziDeck\ProfilesV2\ad349fcc-2aab-4f39-8ba1-af6123d4b185.ulanziProfile"
    r"\Profiles\289c56a8-25c6-49aa-b6fb-7cd4d2db8c90\manifest.json"
)
ORIGINAL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "backup", "dial-profile-original.json")
PREFIX = "com.ulanzi.ulanzistudio.sdroxide."

# Positions: top row 0_0 1_0 2_0 left to right, sides 0_1 left upper, 2_1 left lower,
# 1_1 right upper, 1_2 right lower, knob 0_2. Titles show both meanings, for Studio's window.
KEYS = {
    "0_0": ("radio2m", "2 m / Poprzedni"),
    "1_0": ("scan", "Skan / Play"),
    "2_0": ("radio70cm", "70 cm / Nastepny"),
    "0_1": ("show", "Oxide"),
    "2_1": ("hide", "Pulpit / Kopiuj"),
    "1_1": ("volup", "Vol+ / Redo"),
    "1_2": ("voldown", "Vol- / Undo"),
}
KNOB = {"0_2": ("vfo", "VFO / Glosnosc")}


def entry(action, title):
    return {
        "Action": PREFIX + action,
        "ActionID": str(uuid.uuid4()),
        "ActionParam": {},
        "LinkedTitle": True,
        "Name": title,
        "State": 0,
        "ViewParam": [{"IconRel": "", "Text": title}],
    }


def main():
    running = subprocess.run(
        ["tasklist", "/fi", "imagename eq UlanziDeck.exe", "/nh"], capture_output=True, text=True
    ).stdout
    if "UlanziDeck.exe" in running:
        sys.exit("Najpierw zamknij Ulanzi Studio.")

    with open(ORIGINAL, encoding="utf-8") as f:
        page = json.load(f)
    for controller in page["Controllers"]:
        chosen = KNOB if controller["Type"] == "Encoder" else KEYS
        for pos, (action, title) in chosen.items():
            controller["Actions"][pos] = entry(action, title)
    with open(PAGE, "w", encoding="utf-8", newline="") as f:
        json.dump(page, f, ensure_ascii=False, separators=(",", ":"))
    print("zapisano:", ", ".join(f"{pos}={a}" for pos, (a, _) in {**KEYS, **KNOB}.items()))


if __name__ == "__main__":
    main()

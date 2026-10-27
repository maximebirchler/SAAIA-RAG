"""Qualified loopback recorder, stopping after the first real writer envelope.

There is no HTTP client here. The wire fixture captures the bytes before parsing.
Its deliberate writer rejection is a preflight stop, never a model answer.
"""
import argparse
import json
import pathlib
import sys
from http.server import ThreadingHTTPServer

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "validation-wire"))
import fixture_wire
from verify_wire import extended_windows_path


def preflight_reply(body, control, turn, ordinal):
    if fixture_wire.planner(body):
        return {"kind": "answer", "text": json.dumps({"queries": [{"query": control["preflightQuery"], "topK": 8}]})}
    raise ValueError("planning_preflight_stop_after_writer_capture")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--control", type=pathlib.Path, required=True)
    parser.add_argument("--capture", type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.capture = extended_windows_path(args.capture)
    args.control = extended_windows_path(args.control)
    if not 1024 <= args.port <= 65535 or args.port == 5432 or args.capture.exists():
        raise SystemExit("A fresh loopback capture directory and unprivileged port are required.")
    args.capture.mkdir(parents=True)
    fixture_wire.scripted_reply = preflight_reply
    server = ThreadingHTTPServer(("127.0.0.1", args.port), fixture_wire.Fixture)
    server.control, server.capture = args.control, args.capture
    server.serve_forever()

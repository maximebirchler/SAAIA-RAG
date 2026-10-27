The public `prepare_owned_linux_bootstrap_v6.py` is the complete, byte-exact
v6 owned Linux bootstrap admitted by SHA256
`23f201c39c4d447979d44ae223a1447d382e997ab396b9bf0f816cd0f37046a0`.
Its source is now available in a clean checkout for offline policy tests.
The nested `.gitattributes` preserves those bytes on Windows and Linux.

Importing this module performs no file, process or network operation. Its real
CLI still requires explicit owned paths, preparation identities, signed
configuration and `--execute` before Docker operations. The source contains no
private document, real credential or private key; runtime credentials are
generated inside the explicit preparation function.

This promotion does not change any frozen campaign CLI, archive, import path,
signature or running ingestion. `Test-PlanningOffline.ps1` runs the public
contract tests without historical artifacts or a backend assembly. A green
offline test does not validate ingestion, a paid provider or meal planning.

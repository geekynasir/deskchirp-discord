# Shared fixtures

`presence-cases.json` describes the same input/output policy for both native implementations. `suspended` represents any inactive session gate (including lock). A null expected activity means clear or remain disconnected, never publish a generic fallback activity. Test the serialized payload as well as the headline so extra metadata cannot slip through.

`ipc-frames.json` supplies fixed UTF-8 JSON strings and complete wire bytes. The emoji case deliberately makes character count differ from UTF-8 byte count. Tests should exercise packing and feed each encoded frame to the parser at every possible split point, one byte at a time, and with multiple frames combined. Oversized and invalid lengths need rejection tests in addition to these valid examples.

Application IDs in these files are synthetic test data. They are not defaults or product-owned Discord applications. Users must supply their own public application ID until the product has a registered identity.

Native protocol/lifecycle tests also need fake local transports and scripted READY, acknowledgment, ERROR, EOF, and timeout responses. These fixtures do not replace target-platform checks with a real Discord client.

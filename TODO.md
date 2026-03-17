# TODO

- Generalize canonical catalog cleanup beyond the Bolt seed.
- Add validation for canonical definitions: duplicate ids, conflicting family ids, malformed bindings, inconsistent dimensions.
- Improve semantic normalization of alternate bindings for the same signal.
- Add a second vehicle or platform seed to exercise the shared model outside GM Bolt data.
- Split compatibility/import-export adapters into smaller files as the shared library grows.
- Decide how computed signals should reference dependencies in the canonical model.
- Add PostgreSQL persistence adapters once the canonical model stabilizes further.

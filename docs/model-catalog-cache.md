# Local AI-model catalog

The [catalog coordinator](../Core/AIHappey.Desktop.Core/ModelCatalog.cs) returns a
profile-local snapshot before waiting for gateway discovery. One background
refresh is shared by the model/provider overview and chat/media pickers. A
successful refresh updates the UI without owning the global busy state; a failed
refresh leaves the last usable catalog available. Explicit fresh discovery is
retained for retry, settings discovery, credential changes, and launch-time
revalidation.

The [snapshot store](../Core/AIHappey.Desktop.Core/ModelCatalogStore.cs) keeps
complete V1 response JSON, not serialized display records, below the profile's
models directory. Version, source, and refresh time live outside the model
documents. Unknown response/model fields survive storage. Reads are bounded at
32 MiB; writes use a unique temporary file and atomic replacement. Missing,
corrupt, unsupported, or unavailable storage falls back to network discovery.

The [session scope](../Core/AIHappey.Desktop.Core/Runtime.cs:148) includes profile,
account identity, AI runtime mode/endpoint, and a non-secret credential
fingerprint. Managed runtimes use a stable identity rather than an ephemeral
localhost port. Conversation storage and agent endpoint settings are unrelated
to this cache's identity.

Hosts with catalog-affecting credentials provide
[ModelCatalogCredentialScope](../Core/AIHappey.Desktop.Core/Runtime.cs:108).
The header-auth host loads its credentials during initialization and hashes a
canonical header collection; credential values are never written into catalog
keys. Account, endpoint, or credential changes cancel obsolete refresh
generations. Sign-out is still handled by the existing sign-in lifecycle.

Cached discovery is not authorization: normal authenticated inference and fresh
launch validation are unchanged. Agent catalogs, model editing, user metadata,
and server-side Azure Blob caching are outside this feature. Any future
user-owned model metadata should be separate from replaceable discovery JSON.

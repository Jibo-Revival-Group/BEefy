-- Loop member profile picture metadata (bytes live in IMediaContentStore).
ALTER TABLE LoopMembers
    ADD COLUMN IF NOT EXISTS PhotoContentHash TEXT NULL,
    ADD COLUMN IF NOT EXISTS PhotoContentType TEXT NULL,
    ADD COLUMN IF NOT EXISTS PhotoUpdatedUtc TIMESTAMPTZ NULL;

-- Fantasy EPL League Manager — Database Migrations
-- V012: Seed data.
--
-- BR-011: the profile icon catalog is application-controlled — a User must be able to select a
-- default icon at registration (BR-006), so at least one seeded, active icon must exist before
-- F-001.1 can be exercised end-to-end. Names/asset paths below are placeholders; swap
-- asset_identifier for real, deployed asset paths before go-live — this seed only guarantees the
-- catalog is non-empty, not that these are the final icon assets.

INSERT INTO profile_icons (name, asset_identifier, is_active, sort_order) VALUES
  ('Lion',    'icons/profile/lion.svg',    true, 1),
  ('Eagle',   'icons/profile/eagle.svg',   true, 2),
  ('Fox',     'icons/profile/fox.svg',     true, 3),
  ('Wolf',    'icons/profile/wolf.svg',    true, 4),
  ('Falcon',  'icons/profile/falcon.svg',  true, 5),
  ('Panther', 'icons/profile/panther.svg', true, 6),
  ('Hawk',    'icons/profile/hawk.svg',    true, 7),
  ('Bull',    'icons/profile/bull.svg',    true, 8),
  ('Shark',   'icons/profile/shark.svg',   true, 9),
  ('Stag',    'icons/profile/stag.svg',    true, 10);

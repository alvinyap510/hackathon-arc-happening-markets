.PHONY: e2e up down clean

# Bring up the stack (anvil + deploy + backend), run the driver, tear down.
e2e:
	docker compose up -d --build
	docker compose run --rm driver
	docker compose down

# Bring the stack up only (anvil + deploy + backend green).
up:
	docker compose up -d --build

down:
	docker compose down

# Full teardown including the generated runtime files.
clean:
	docker compose down -v
	rm -rf e2e/.runtime contracts/addresses.json

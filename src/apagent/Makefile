VERSION ?= dev

.PHONY: build build-ap build-local test vet clean

# Build for UniFi APs (linux/arm/v7, static binary). Every measured U7-class AP is armv7l;
# an arm64 build will not exec on them, so there is deliberately no arm64 target.
build-ap:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-arm .

# Build for local machine (for testing)
build-local:
	go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent .

# Build both targets
build: build-ap build-local

test:
	go test ./...

vet:
	go vet ./...

clean:
	rm -rf bin/

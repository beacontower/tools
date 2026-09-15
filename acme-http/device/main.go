// acme-http-device is a BeaconTower device simulator that speaks plain HTTP.
//
// It exists to show the smallest honest implementation of a device: it POSTs
// telemetry and reported properties up to a provider, and it SERVES a webhook
// so the provider can push commands and desired properties back down. Those
// two halves are the whole contract. Everything here is the standard library.
//
// A device is not a client that polls. The provider has to be able to reach it,
// which is why this listens as well as posts - a poll-only device cannot answer
// a command while it is asleep, and the platform would have to guess.
//
//	acme-http-device -provider http://localhost:5080 -id acme-01
//
// See ../README.md for the wire contract and ../provider for the other end.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"math"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"
)

type device struct {
	provider string
	id       string
	key      string
	client   *http.Client

	mu       sync.Mutex
	desired  map[string]any
	reported map[string]any
}

func main() {
	var (
		provider = flag.String("provider", "http://localhost:5080", "provider base URL")
		id       = flag.String("id", "acme-01", "device id")
		listen   = flag.String("listen", "127.0.0.1:7070", "address to serve the webhook on")
		interval = flag.Duration("interval", 5*time.Second, "telemetry interval")
		count    = flag.Int("count", 0, "stop after n telemetry messages (0 = run until interrupted)")
	)
	flag.Parse()

	d := &device{
		provider: *provider,
		id:       *id,
		client:   &http.Client{Timeout: 10 * time.Second},
		desired:  map[string]any{},
		reported: map[string]any{"firmware": "1.0.0"},
	}

	// The webhook comes up first. Registering before we can answer would give
	// the provider a callback URL that refuses connections, and a command
	// arriving in that window would be reported as a device error rather than
	// as the race it is.
	ln, err := net.Listen("tcp", *listen)
	if err != nil {
		log.Fatalf("cannot listen on %s: %v", *listen, err)
	}
	srv := &http.Server{Handler: d.webhook()}
	go func() {
		if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed {
			log.Fatalf("webhook: %v", err)
		}
	}()
	callback := "http://" + ln.Addr().String()
	log.Printf("webhook listening on %s", callback)

	if err := d.register(callback); err != nil {
		log.Fatalf("register: %v", err)
	}
	log.Printf("registered %s with %s", d.id, d.provider)

	if err := d.report(); err != nil {
		log.Printf("initial properties: %v", err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	ticker := time.NewTicker(*interval)
	defer ticker.Stop()
	for sent := 0; ; {
		select {
		case <-ctx.Done():
			log.Print("stopping")
			shutdown, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			_ = srv.Shutdown(shutdown)
			return
		case <-ticker.C:
			// A plausible reading rather than a constant, so a chart of it
			// looks like something and an off-by-one in a flow is visible.
			celsius := 20 + 2*math.Sin(float64(sent)/6)
			if err := d.telemetry(map[string]any{"celsius": round(celsius)}); err != nil {
				log.Printf("telemetry: %v", err)
				continue
			}
			sent++
			log.Printf("sent celsius=%.2f (%d)", celsius, sent)
			if *count > 0 && sent >= *count {
				log.Printf("sent %d message(s), stopping", sent)
				shutdown, cancel := context.WithTimeout(context.Background(), 2*time.Second)
				defer cancel()
				_ = srv.Shutdown(shutdown)
				return
			}
		}
	}
}

func round(f float64) float64 { return math.Round(f*100) / 100 }

// --- device -> provider ----------------------------------------------------

// register hands the provider a URL it can reach us on and gets a device key
// back. A real provider mints that key in OnProvisionAsync, inside the
// provisioning transaction; this endpoint is the simulator's stand-in for it.
func (d *device) register(callback string) error {
	body, err := d.post("/api/devices/"+d.id+"/register",
		map[string]any{"callback": callback}, false)
	if err != nil {
		return err
	}
	var out struct {
		Key string `json:"key"`
	}
	if err := json.Unmarshal(body, &out); err != nil {
		return fmt.Errorf("decoding register response: %w", err)
	}
	if out.Key == "" {
		return fmt.Errorf("provider returned no device key")
	}
	d.key = out.Key
	return nil
}

func (d *device) telemetry(values map[string]any) error {
	_, err := d.post("/api/devices/"+d.id+"/telemetry", values, true)
	return err
}

// report sends the properties the DEVICE owns. Desired properties come the
// other way and are echoed back here once applied - that round trip is how the
// platform knows a setting actually took.
func (d *device) report() error {
	d.mu.Lock()
	snapshot := make(map[string]any, len(d.reported))
	for k, v := range d.reported {
		snapshot[k] = v
	}
	d.mu.Unlock()
	_, err := d.post("/api/devices/"+d.id+"/properties", snapshot, true)
	return err
}

func (d *device) post(path string, payload any, auth bool) ([]byte, error) {
	buf, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequest(http.MethodPost, d.provider+path, bytes.NewReader(buf))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")
	if auth {
		req.Header.Set("Authorization", "Bearer "+d.key)
	}
	resp, err := d.client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode >= 300 {
		return nil, fmt.Errorf("%s %s: %s: %s", req.Method, path, resp.Status, bytes.TrimSpace(body))
	}
	return body, nil
}

// --- provider -> device ----------------------------------------------------

func (d *device) webhook() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /commands", d.onCommand)
	mux.HandleFunc("POST /desired", d.onDesired)
	return mux
}

// command is what the provider sends; result is what it expects back. The
// fields line up with the SDK's CommandResult, and Status must be one of its
// CommandStatus names - "Ok" and "DeviceError" are the two a device decides.
type command struct {
	CorrelationID string          `json:"correlationId"`
	Name          string          `json:"name"`
	Payload       json.RawMessage `json:"payload"`
}

type result struct {
	CorrelationID string `json:"correlationId"`
	Status        string `json:"status"`
	ErrorMessage  string `json:"errorMessage,omitempty"`
}

func (d *device) onCommand(w http.ResponseWriter, r *http.Request) {
	var c command
	if err := json.NewDecoder(r.Body).Decode(&c); err != nil {
		http.Error(w, "malformed command", http.StatusBadRequest)
		return
	}
	res := result{CorrelationID: c.CorrelationID, Status: "Ok"}
	switch c.Name {
	case "ping":
	case "reboot":
		d.mu.Lock()
		boots, _ := d.reported["bootCount"].(int)
		d.reported["bootCount"] = boots + 1
		d.mu.Unlock()
		go func() {
			if err := d.report(); err != nil {
				log.Printf("reporting after reboot: %v", err)
			}
		}()
	default:
		// A device that does not know a command says so. Answering Ok to
		// everything makes the platform believe work happened.
		res.Status = "DeviceError"
		res.ErrorMessage = "unknown command: " + c.Name
	}
	log.Printf("command %s -> %s", c.Name, res.Status)
	writeJSON(w, http.StatusOK, res)
}

func (d *device) onDesired(w http.ResponseWriter, r *http.Request) {
	var props map[string]any
	if err := json.NewDecoder(r.Body).Decode(&props); err != nil {
		http.Error(w, "malformed properties", http.StatusBadRequest)
		return
	}
	d.mu.Lock()
	for k, v := range props {
		d.desired[k] = v
		// Applied immediately, and echoed back as reported below. A device
		// that cannot apply a setting should report what it DID apply, not
		// what it was asked for.
		d.reported[k] = v
	}
	d.mu.Unlock()
	log.Printf("desired properties applied: %v", props)
	w.WriteHeader(http.StatusNoContent)
	go func() {
		if err := d.report(); err != nil {
			log.Printf("reporting desired: %v", err)
		}
	}()
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

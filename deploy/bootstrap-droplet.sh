#!/usr/bin/env bash
#
# One-time provisioning of the staging droplet (Ubuntu 24.04).
#
# Run as root, once, on a fresh droplet:
#   bash bootstrap-droplet.sh
#
# It is idempotent: re-running it does not duplicate users, rules or packages. It never receives a
# password: SSH access is key-only, and the deployment user is the one GitHub Actions connects as.

set -euo pipefail

DEPLOY_USER="${DEPLOY_USER:-deploy}"

# Public key only. This is not a secret: the private half stays on the operator's machine and in the
# GitHub `staging` environment secret.
PUBLIC_KEY="${PUBLIC_KEY:-ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJawiLzwx6ptlyO/MYmY42lZADuQjhXtWem8eVImX7UE convenciones-staging-deploy}"

export DEBIAN_FRONTEND=noninteractive

echo "== packages =="
apt-get update -y
apt-get upgrade -y
apt-get install -y ca-certificates curl gnupg ufw unattended-upgrades

echo "== clock =="
timedatectl set-timezone UTC

echo "== docker =="
if ! command -v docker >/dev/null 2>&1; then
    curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker

echo "== deploy user =="
if ! id -u "$DEPLOY_USER" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/bash "$DEPLOY_USER"
fi
usermod -aG docker "$DEPLOY_USER"

install -d -m 700 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "/home/$DEPLOY_USER/.ssh"
printf '%s\n' "$PUBLIC_KEY" > "/home/$DEPLOY_USER/.ssh/authorized_keys"
chown "$DEPLOY_USER:$DEPLOY_USER" "/home/$DEPLOY_USER/.ssh/authorized_keys"
chmod 600 "/home/$DEPLOY_USER/.ssh/authorized_keys"

install -d -m 755 -o "$DEPLOY_USER" -g "$DEPLOY_USER" /opt/convenciones

echo "== ssh: keys only =="
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sed -i 's/^#\?KbdInteractiveAuthentication.*/KbdInteractiveAuthentication no/' /etc/ssh/sshd_config
sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin prohibit-password/' /etc/ssh/sshd_config
systemctl reload ssh

echo "== firewall: 22 and 80 only =="
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw --force enable

echo "== automatic security upgrades =="
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF

echo "== result =="
docker --version
docker compose version
ufw status verbose
id "$DEPLOY_USER"

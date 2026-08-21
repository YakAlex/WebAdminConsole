import { Bell, Send } from 'lucide-react'
import { PageHeader } from '@/components/ui/PageHeader'
import { CredentialCard } from '@/components/settings/CredentialCard/CredentialCard'
import { TelegramUsersTable } from '@/components/settings/TelegramUsersTable/TelegramUsersTable'
import { MonitoringTogglesCard } from '@/components/settings/MonitoringTogglesCard/MonitoringTogglesCard'
import { useSettingsViewModel } from './useSettingsViewModel'
import styles from './Settings.module.scss'

/**
 * T6.2: Settings — Credentials (Zabbix Token, Telegram Bot Token) +
 * Telegram Users (дозволені chat_id). Дані/дії — REST через
 * useSettingsViewModel(), без SignalR (рідкісні, явні дії адміна).
 */
export function Settings() {
  const vm = useSettingsViewModel()

  return (
    <div className={styles.root}>
      <PageHeader title="Settings" subtitle="External API credentials and Telegram bot access." />

      {vm.error && <div className={styles.error}>Failed to load settings: {vm.error.message}</div>}

      {vm.toggles && (
        <MonitoringTogglesCard
          zabbixEnabled={vm.toggles.zabbixMonitoringEnabled}
          rdpEnabled={vm.toggles.rdpMonitoringEnabled}
          backupEnabled={vm.toggles.backupMonitoringEnabled}
          onToggle={vm.toggleMonitoring}
          saving={vm.togglesSaving}
        />
      )}

      <div className={styles.credentialsRow}>
        <CredentialCard
          title="Zabbix Token"
          icon={<Bell size={14} strokeWidth={1.75} />}
          description="API token used to poll active problems from Zabbix."
          hasCredentials={vm.credentials?.zabbix.hasCredentials ?? false}
          maskedValue={vm.credentials?.zabbix.maskedSecret ?? ''}
          inputLabel="Zabbix API token"
          onSave={vm.saveZabbix}
          onClear={vm.clearZabbix}
        />
        <CredentialCard
          title="Telegram Bot Token"
          icon={<Send size={14} strokeWidth={1.75} />}
          description="Bot token from @BotFather, used for the Telegram admin bot."
          hasCredentials={vm.credentials?.telegram.hasCredentials ?? false}
          maskedValue={vm.credentials?.telegram.maskedToken ?? ''}
          inputLabel="Telegram bot token"
          onSave={vm.saveTelegram}
          onClear={vm.clearTelegram}
        />
      </div>

      <TelegramUsersTable users={vm.users} onAdd={vm.addUser} onRemove={vm.removeUser} />
    </div>
  )
}

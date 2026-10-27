"""Opt-in table work settings; the legacy repair options remain unchanged."""
from dataclasses import asdict, dataclass, fields

from .table_repair_options import TableRepairOptions


@dataclass(frozen=True)
class TableObservationWorkOptions(TableRepairOptions):
    alternative_readings_enabled: bool = True
    repair_enabled: bool = True

    def __post_init__(self):
        super().__post_init__()
        if type(self.alternative_readings_enabled) is not bool or type(self.repair_enabled) is not bool:
            raise ValueError("Table observation activation invalid.")

    @classmethod
    def from_table_options(cls, options, *, repair_enabled):
        return cls(**asdict(options), repair_enabled=repair_enabled)


def base_table_options(options):
    """The child accepts only its existing table-engine settings."""
    return {field.name: getattr(options, field.name) for field in fields(TableRepairOptions)}


def observation_settings(options):
    return {**base_table_options(options), "schema_version": "saaia_table_observation_settings_v1",
        "enabled": True, "repair_enabled": getattr(options, "repair_enabled", True),
        "selection_policy": "sparse_then_descending_table_tail_v1",
        "budget_scope": "shared_regional_table_work",
        "process_isolation": "per_region_linux_process_group",
        "engine": "rapidocr", "primary_projection": False}


def requested_table_observations(fields):
    values = [value for name, value in fields if name == "saaia_table_observations_enabled"]
    if not values:
        return False
    if len(values) != 1 or values[0] not in {"true", "false"}:
        raise ValueError("table_observation_request_invalid")
    return values[0] == "true"
